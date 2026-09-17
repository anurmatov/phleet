# Fleet.Comms — the north boundary listener.
#
# Build config only. There is deliberately no port, certificate, hostname or credential here:
# those are deployment decisions and this is a public repository (issue #292 scope 7). The
# listener address comes from the environment at run time (ASPNETCORE_URLS), and TLS is
# terminated in front of this process — there is no plaintext listener by contract
# (docs/first-party-api.md §16).
#
# Released 10.0 tags, matching the images the agent and orchestrator already build from.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Fleet.Comms/Fleet.Comms.csproj src/Fleet.Comms/
COPY src/Fleet.Protocol/Fleet.Protocol.csproj src/Fleet.Protocol/
RUN dotnet restore src/Fleet.Comms/Fleet.Comms.csproj

COPY src/ src/
RUN dotnet publish src/Fleet.Comms/Fleet.Comms.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "Fleet.Comms.dll"]
