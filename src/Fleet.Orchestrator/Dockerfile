FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Fleet.Orchestrator/Fleet.Orchestrator.csproj src/Fleet.Orchestrator/
COPY src/Fleet.Shared/Fleet.Shared.csproj src/Fleet.Shared/
RUN dotnet restore src/Fleet.Orchestrator/Fleet.Orchestrator.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Orchestrator/Fleet.Orchestrator.csproj -c Release -o /app
# Workflow failure accountability (#476) build guard: the published assembly must carry the outcome
# services, so an image built from a tree without them fails here instead of starting a stack whose
# observer silently does not exist. Type names live in the metadata string heap as UTF-8.
RUN for type in WorkflowOutcomeReconciler WorkflowOutcomeInboxService WorkflowRecoveryService \
      WorkflowOutcomeAuthorization WorkflowOutcomeEndpoints DataProtectionWorkflowOutcomePayloadProtector; do \
      grep -qaF "$type" /app/Fleet.Orchestrator.dll || { echo "ERROR: Fleet.Orchestrator.dll lacks $type (#476)"; exit 1; }; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ARG COMPOSE_VERSION=v2.29.7
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/* \
  && ARCH=$(uname -m) \
  && curl -fsSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-${ARCH}" \
       -o /usr/local/bin/docker-compose \
  && chmod +x /usr/local/bin/docker-compose

COPY --from=build /app .
COPY src/Fleet.Orchestrator/projects /app/projects
COPY src/Fleet.Orchestrator/roles /app/roles

# Workflow failure accountability (#476): mount point of the workflow_outcomes_keys volume. Created
# here, empty, owner-only, so a fresh named volume inherits this owner and mode 0700 on first mount.
# Keys are never baked into the image. There is no USER directive, so the build user IS the runtime
# user; the guard fails the build if the mode or owner did not take.
RUN install -d -m 0700 /var/lib/phleet/workflow-outcomes/keys \
  && [ "$(stat -c '%a %u' /var/lib/phleet/workflow-outcomes/keys)" = "700 $(id -u)" ] \
  && [ -z "$(ls -A /var/lib/phleet/workflow-outcomes/keys)" ] \
  || { echo "ERROR: workflow-outcomes keyring mount point is not an empty 0700 runtime-owned directory (#476)"; exit 1; }

EXPOSE 3600

ENTRYPOINT ["dotnet", "Fleet.Orchestrator.dll"]
