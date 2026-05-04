FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Fleet.Agent/Fleet.Agent.csproj src/Fleet.Agent/
RUN dotnet restore src/Fleet.Agent/Fleet.Agent.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Agent/Fleet.Agent.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app

RUN apt-get update && apt-get install -y curl git jq rsync cron openssh-client && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    npm install -g @anthropic-ai/claude-code
# Fail the build early if the installed claude version does not support --append-system-prompt-file.
# This flag is required by PromptBuilder.WriteSystemPromptFile() to avoid E2BIG failures.
# The flag isn't listed as a standalone help entry — it appears inside --bare's description,
# so we grep for the broader 'append-system-prompt' pattern.
RUN claude --help 2>&1 | grep -q 'append-system-prompt' || \
    (echo "ERROR: installed claude version does not support --append-system-prompt" && exit 1)
RUN (curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg) && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | tee /etc/apt/sources.list.d/github-cli.list > /dev/null && \
    apt-get update && apt-get install -y gh && rm -rf /var/lib/apt/lists/*

# Docker CLI (client only — daemon runs on the host)
RUN curl -fsSL https://download.docker.com/linux/static/stable/$(uname -m)/docker-27.5.1.tgz \
    | tar xz --strip-components=1 -C /usr/local/bin docker/docker

# MinIO client for file sharing via fleet-minio
RUN ARCH=$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/') && \
    curl -fsSL "https://dl.min.io/client/mc/release/linux-${ARCH}/mc" -o /usr/local/bin/mc && \
    chmod +x /usr/local/bin/mc

ARG GIT_COMMIT=unknown
ENV FLEET_BUILD_COMMIT=$GIT_COMMIT

COPY --from=build /app .
COPY src/Fleet.Agent/codex-bridge.mjs /app/codex-bridge.mjs
RUN cd /app && npm install @openai/codex-sdk@0.118.0

# Gemini CLI — headless mode. OAuth credentials mounted writable at runtime by the orchestrator.
# Pinned to @google/gemini-cli@0.40.1 to match the verified flag set (--output-format stream-json,
# --yolo, GEMINI_SYSTEM_MD env var) and the stream-json event schema used by GeminiExecutor.cs.
RUN npm install -g @google/gemini-cli@0.40.1
# Build-time guard: verify gemini CLI is on PATH and responds to --version.
RUN gemini --version || (echo 'ERROR: gemini CLI not on PATH — npm install -g may have failed' && exit 1)
# Build-time guard: @google/gemini-cli-core SDK must NOT be installed globally.
# GeminiExecutor.cs uses the CLI binary (not the SDK). The SDK was used by the previous
# bridge approach (issue #128, PR #129) and must not be re-introduced accidentally.
RUN npm list -g @google/gemini-cli-core 2>&1 | grep -q "empty" || \
    (echo 'ERROR: @google/gemini-cli-core SDK is installed — remove it; GeminiExecutor uses the CLI binary only' && exit 1)

RUN mkdir -p /workspace /root/.claude

COPY skills/ /app/skills/

COPY gh-auth.sh /app/gh-auth.sh
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh /app/gh-auth.sh
ENTRYPOINT ["/app/entrypoint.sh"]
