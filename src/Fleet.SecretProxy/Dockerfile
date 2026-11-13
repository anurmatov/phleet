# The released runtime, not a preview: this service terminates and originates TLS.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Build settings and package versions shared by every project; restore needs both.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Fleet.SecretProxy/Fleet.SecretProxy.csproj src/Fleet.SecretProxy/
COPY src/Fleet.Shared/Fleet.Shared.csproj src/Fleet.Shared/
RUN dotnet restore src/Fleet.SecretProxy/Fleet.SecretProxy.csproj
COPY src/Fleet.SecretProxy/ src/Fleet.SecretProxy/
COPY src/Fleet.Shared/ src/Fleet.Shared/
RUN dotnet publish src/Fleet.SecretProxy/Fleet.SecretProxy.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# openssh-client: ssh-agent and ssh-add hold and constrain bound SSH keys (SshAgentGate).
RUN apt-get update && apt-get install -y curl openssh-client && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# 443: the forwarder, reached only by granted agents on the internal secrets network.
# 8080: /health, loopback only.
EXPOSE 443

ENTRYPOINT ["dotnet", "Fleet.SecretProxy.dll"]
