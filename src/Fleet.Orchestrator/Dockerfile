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

COPY --from=build /app .
COPY src/Fleet.Orchestrator/projects /app/projects
COPY src/Fleet.Orchestrator/roles /app/roles

EXPOSE 3600

ENTRYPOINT ["dotnet", "Fleet.Orchestrator.dll"]
