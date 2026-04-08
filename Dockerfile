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

RUN mkdir -p /workspace /root/.claude

COPY skills/ /app/skills/

COPY gh-auth.sh /app/gh-auth.sh
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh /app/gh-auth.sh
ENTRYPOINT ["/app/entrypoint.sh"]
