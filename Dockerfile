FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Fleet.Agent/Fleet.Agent.csproj src/Fleet.Agent/
RUN dotnet restore src/Fleet.Agent/Fleet.Agent.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Agent/Fleet.Agent.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app

ARG CLAUDE_CODE_VERSION=2.1.280
ARG CODEX_CLI_VERSION=0.153.4
ARG GEMINI_CLI_VERSION=0.40.1
ARG MC_VERSION=RELEASE.2025-08-13T08-35-41Z

RUN apt-get update && apt-get install -y curl git jq rsync cron openssh-client && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} @openai/codex@${CODEX_CLI_VERSION}
# Fail the build if npm resolved a different Claude CLI than the verified pin.
RUN ACTUAL_CLAUDE_VERSION="$(claude --version | awk '{print $1}')" && \
    echo "Installed claude ${ACTUAL_CLAUDE_VERSION}" && \
    [ "$ACTUAL_CLAUDE_VERSION" = "$CLAUDE_CODE_VERSION" ] || \
    (echo "ERROR: expected claude ${CLAUDE_CODE_VERSION}, got ${ACTUAL_CLAUDE_VERSION}" && exit 1)
# Fail the build early if the installed claude version does not support --append-system-prompt-file.
# This flag is required by PromptBuilder.WriteSystemPromptFile() to avoid E2BIG failures.
# The flag isn't listed as a standalone help entry — it appears inside --bare's description,
# so we grep for the broader 'append-system-prompt' pattern.
RUN claude --help 2>&1 | grep -q 'append-system-prompt' || \
    (echo "ERROR: installed claude version does not support --append-system-prompt" && exit 1)
RUN ACTUAL_CODEX_VERSION="$(codex --version | awk '{print $2}')" && \
    echo "Installed codex ${ACTUAL_CODEX_VERSION}" && \
    [ "$ACTUAL_CODEX_VERSION" = "$CODEX_CLI_VERSION" ] || \
    (echo "ERROR: expected codex ${CODEX_CLI_VERSION}, got ${ACTUAL_CODEX_VERSION}" && exit 1)
RUN codex app-server --help > /dev/null 2>&1 || \
    (echo "ERROR: installed codex version does not support app-server" && exit 1)
RUN (curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg) && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | tee /etc/apt/sources.list.d/github-cli.list > /dev/null && \
    apt-get update && apt-get install -y gh && rm -rf /var/lib/apt/lists/*

# Docker CLI (client only — daemon runs on the host)
RUN curl -fsSL https://download.docker.com/linux/static/stable/$(uname -m)/docker-27.5.1.tgz \
    | tar xz --strip-components=1 -C /usr/local/bin docker/docker

# MinIO client for file sharing via fleet-minio
# dl.min.io returns 410 (#330) and quay.io/minio refuses anonymous pulls (401, #363), so the
# binary comes from the pinned GitHub release. The sha256 values live here, never fetched from
# the same release at build time; a new MC_VERSION needs both values replaced with it.
RUN ARCH="$(dpkg --print-architecture)" && \
    case "$ARCH" in \
      amd64) MC_SHA256=01f866e9c5f9b87c2b09116fa5d7c06695b106242d829a8bb32990c00312e891 ;; \
      arm64) MC_SHA256=14c8c9616cfce4636add161304353244e8de383b2e2752c0e9dad01d4c27c12c ;; \
      *) echo "ERROR: no pinned mc sha256 for architecture ${ARCH}" && exit 1 ;; \
    esac && \
    curl -fsSL "https://github.com/minio/mc/releases/download/${MC_VERSION}/mc.linux-${ARCH}.${MC_VERSION}" \
      -o /usr/local/bin/mc && \
    echo "${MC_SHA256}  /usr/local/bin/mc" | sha256sum -c - && \
    chmod +x /usr/local/bin/mc
RUN mc --version | grep -q "version ${MC_VERSION} " || \
    (echo "ERROR: expected mc ${MC_VERSION}" && mc --version && exit 1)

ARG GIT_COMMIT=unknown
ENV FLEET_BUILD_COMMIT=$GIT_COMMIT

COPY --from=build /app .

# Gemini CLI — headless mode. OAuth credentials mounted writable at runtime by the orchestrator.
# Pinned to the GEMINI_CLI_VERSION arg to match the verified flag set (--output-format stream-json,
# --yolo, GEMINI_SYSTEM_MD env var) and the stream-json event schema used by GeminiExecutor.cs.
RUN npm install -g @google/gemini-cli@${GEMINI_CLI_VERSION}
# Build-time guard: verify gemini CLI is on PATH and responds to --version.
RUN gemini --version || (echo 'ERROR: gemini CLI not on PATH — npm install -g may have failed' && exit 1)
RUN ACTUAL_GEMINI_VERSION="$(gemini --version | awk '{print $1}')" && \
    echo "Installed gemini ${ACTUAL_GEMINI_VERSION}" && \
    [ "$ACTUAL_GEMINI_VERSION" = "$GEMINI_CLI_VERSION" ] || \
    (echo "ERROR: expected gemini ${GEMINI_CLI_VERSION}, got ${ACTUAL_GEMINI_VERSION}" && exit 1)
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
