# Fleet.Comms — the north boundary listener.
#
# Build config only. There is deliberately no port, certificate, hostname or credential here:
# those are deployment decisions and this is a public repository (issue #292 scope 7). The
# listener address comes from the environment at run time (ASPNETCORE_URLS), and TLS is
# terminated in front of this process — there is no plaintext listener by contract
# (docs/first-party-api.md §16).
#
# Released 10.0 tags, matching the images the agent and orchestrator already build from.
#
# There is no credential store in this image (#530): the orchestrator owns sign-in, and this service
# asks it who each caller is. Nothing here needs a volume.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Build settings and package versions shared by every project; restore needs both.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Fleet.Comms/Fleet.Comms.csproj src/Fleet.Comms/
COPY src/Fleet.Protocol/Fleet.Protocol.csproj src/Fleet.Protocol/
COPY src/Fleet.Contracts/Fleet.Contracts.csproj src/Fleet.Contracts/
COPY src/Fleet.Conversations/Fleet.Conversations.csproj src/Fleet.Conversations/
COPY src/Fleet.Conversations.Contracts/Fleet.Conversations.Contracts.csproj src/Fleet.Conversations.Contracts/
COPY src/Fleet.Shared/Fleet.Shared.csproj src/Fleet.Shared/
RUN dotnet restore src/Fleet.Comms/Fleet.Comms.csproj

COPY src/ src/
RUN dotnet publish src/Fleet.Comms/Fleet.Comms.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl, for the container HEALTHCHECK. The aspnet:10.0 base image ships neither curl nor wget, so
# the four sibling service images install it for exactly this reason; copying their healthcheck
# line without this layer would produce a probe that can never run — a container permanently
# unhealthy, or worse, a gate everyone learns to ignore.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENTRYPOINT ["dotnet", "Fleet.Comms.dll"]
