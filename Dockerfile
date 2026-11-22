FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Build settings and package versions shared by every project; restore needs both.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Fleet.Agent/Fleet.Agent.csproj src/Fleet.Agent/
COPY src/Fleet.Contracts/Fleet.Contracts.csproj src/Fleet.Contracts/
COPY src/Fleet.Conversations.Contracts/Fleet.Conversations.Contracts.csproj src/Fleet.Conversations.Contracts/
COPY src/Fleet.Journal.Client/Fleet.Journal.Client.csproj src/Fleet.Journal.Client/
COPY src/Fleet.Protocol/Fleet.Protocol.csproj src/Fleet.Protocol/
COPY src/Fleet.Shared/Fleet.Shared.csproj src/Fleet.Shared/
COPY src/Fleet.Shared.Messaging/Fleet.Shared.Messaging.csproj src/Fleet.Shared.Messaging/
COPY src/Fleet.Shared.Telegram/Fleet.Shared.Telegram.csproj src/Fleet.Shared.Telegram/
RUN dotnet restore src/Fleet.Agent/Fleet.Agent.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Agent/Fleet.Agent.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app

ARG CLAUDE_CODE_VERSION=2.1.327
ARG CODEX_CLI_VERSION=0.170.0
ARG AGY_CLI_VERSION=1.5.14
ARG MC_VERSION=RELEASE.2025-08-13T08-35-41Z

# ffmpeg/ffprobe let agents look inside video attachments (#361). DejaVu and fontconfig let
# diagrams agents render show their text, Latin and Cyrillic (#371); ffmpeg pulls both in today,
# and naming them keeps them if ffmpeg changes. --no-install-recommends is scoped to this line
# so ffmpeg's X11/GUI recommends stay out without changing what the other packages pull in.
RUN apt-get update && apt-get install -y curl git jq rsync cron openssh-client python3 && \
    apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core fontconfig && rm -rf /var/lib/apt/lists/*
RUN ffmpeg -version >/dev/null && ffprobe -version >/dev/null || \
    (echo "ERROR: ffmpeg/ffprobe not runnable in the agent image" && exit 1)
RUN fc-list | grep -qi dejavu || (echo "ERROR: no DejaVu font in the agent image" && exit 1)
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

# Antigravity CLI (agy): what a gemini agent runs, with a Gemini API key. A closed binary from the
# pinned GitHub release. The sha256 values of the two release archives live here, never fetched
# from the same release at build time; a new AGY_CLI_VERSION needs both values replaced with it,
# and `bash tests/antigravity-cli/run.sh` (which reads the version and the two values from here)
# run against it: that harness is what says whether the fleet's assumptions about the CLI still hold.
RUN ARCH="$(dpkg --print-architecture)" && \
    case "$ARCH" in \
      amd64) AGY_ASSET=agy_cli_linux_x64.tar.gz; AGY_SHA256=d58386322d2073e96b33f7cc32b4e9ae2db6228f58440369725967b2de48c7b5 ;; \
      arm64) AGY_ASSET=agy_cli_linux_arm64.tar.gz; AGY_SHA256=e5f69a3123fac66974b303d9a201f381a0fb32b3ef869d9139da85d5caf67a2a ;; \
      *) echo "ERROR: no pinned agy sha256 for architecture ${ARCH}" && exit 1 ;; \
    esac && \
    curl -fsSL "https://github.com/google-antigravity/antigravity-cli/releases/download/${AGY_CLI_VERSION}/${AGY_ASSET}" \
      -o /tmp/agy.tar.gz && \
    echo "${AGY_SHA256}  /tmp/agy.tar.gz" | sha256sum -c - && \
    tar xzf /tmp/agy.tar.gz -C /tmp antigravity && \
    install -m 0755 /tmp/antigravity /usr/local/bin/agy && \
    rm -f /tmp/agy.tar.gz /tmp/antigravity
# The CLI replaces itself in the background when a newer release is out, and root can always write
# /usr/local/bin. This is its own switch (only the value "true" works); GeminiExecutor sets it on
# every process too. Without it the pin above holds only until the first start.
ENV AGY_CLI_DISABLE_AUTO_UPDATE=true
RUN ACTUAL_AGY_VERSION="$(agy --version)" && \
    echo "Installed agy ${ACTUAL_AGY_VERSION}" && \
    [ "$ACTUAL_AGY_VERSION" = "$AGY_CLI_VERSION" ] || \
    (echo "ERROR: expected agy ${AGY_CLI_VERSION}, got ${ACTUAL_AGY_VERSION}" && exit 1)

RUN mkdir -p /workspace /root/.claude

COPY skills/ /app/skills/

COPY scripts/acceptance_plan.py /opt/fleet/acceptance_plan.py
RUN test -f /opt/fleet/acceptance_plan.py && python3 /opt/fleet/acceptance_plan.py --help

COPY gh-auth.sh /app/gh-auth.sh
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh /app/gh-auth.sh
# entrypoint.sh reads the agent's configuration through the binary's one-shot mode, never itself.
# Fail the build if that mode is missing or cannot read the baked appsettings.json, which names no
# provider and so must come back as claude.
RUN dotnet Fleet.Agent.dll prepare facts | grep -qx 'PROVIDER=claude' || \
    (echo "ERROR: 'dotnet Fleet.Agent.dll prepare facts' did not report the baked configuration" && exit 1)
ENTRYPOINT ["/app/entrypoint.sh"]
