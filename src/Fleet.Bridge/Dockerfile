FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Build settings and package versions shared by every project; restore needs both.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Fleet.Bridge/Fleet.Bridge.csproj src/Fleet.Bridge/
RUN dotnet restore src/Fleet.Bridge/Fleet.Bridge.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Bridge/Fleet.Bridge.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

EXPOSE 3300

ENTRYPOINT ["dotnet", "Fleet.Bridge.dll"]
