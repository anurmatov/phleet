FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Fleet.Orchestrator/Fleet.Orchestrator.csproj src/Fleet.Orchestrator/
COPY src/Fleet.Shared/Fleet.Shared.csproj src/Fleet.Shared/
RUN dotnet restore src/Fleet.Orchestrator/Fleet.Orchestrator.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Orchestrator/Fleet.Orchestrator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ARG COMPOSE_VERSION=v2.29.7
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/* \
  && ARCH=$(uname -m) \
  && curl -fsSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-${ARCH}" \
       -o /usr/local/bin/docker-compose \
  && chmod +x /usr/local/bin/docker-compose

# Managed stock OpenShell (#492): this image understands managed bindings (rollout guard input).
LABEL org.phleet.managed-stock-runtime=1
# #502: this image understands managed subscription bindings (persisted workspaces, retirement, four-key preflight).
LABEL org.phleet.managed-stock-subscription=1
# Opt-in exact stock CLI. Both build args or neither: the binary must match the SHA-256 and report
# the pinned version. Without them no CLI is installed and managed apply reports stock_cli_missing.
# #503 W6/W7: the pinned CLI's `sandbox upload` spawns the external `ssh` client (openshell-cli ssh.rs
# ssh_upload_command) and, with its default .gitignore filtering, first runs `git rev-parse` (run.rs
# discover_git_repo_root) even outside a repository. The opt-in CLI therefore brings openssh-client
# and git, and the build fails without either. The default image stays without both.
# scripts/test-stock-upload-transport.sh is the upload regression.
ARG OPENSHELL_CLI_URL=
ARG OPENSHELL_CLI_SHA256=
RUN if [ -n "$OPENSHELL_CLI_URL$OPENSHELL_CLI_SHA256" ]; then \
      [ -n "$OPENSHELL_CLI_URL" ] && [ -n "$OPENSHELL_CLI_SHA256" ] || { echo "both OPENSHELL_CLI_URL and OPENSHELL_CLI_SHA256 are required" >&2; exit 1; }; \
      curl -fsSL "$OPENSHELL_CLI_URL" -o /usr/local/bin/openshell \
      && echo "$OPENSHELL_CLI_SHA256  /usr/local/bin/openshell" | sha256sum -c - \
      && chmod 0755 /usr/local/bin/openshell \
      && /usr/local/bin/openshell --version | grep -Eq '^openshell 0\.1\.5([+ ]|$)' \
      && apt-get update && apt-get install -y --no-install-recommends openssh-client git && rm -rf /var/lib/apt/lists/* \
      && command -v ssh >/dev/null && ssh -V && command -v git >/dev/null && git --version; \
    fi

COPY --from=build /app .
COPY src/Fleet.Orchestrator/projects /app/projects
COPY src/Fleet.Orchestrator/roles /app/roles

EXPOSE 3600

ENTRYPOINT ["dotnet", "Fleet.Orchestrator.dll"]
