FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Build settings and package versions shared by every project; restore needs both.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Fleet.Telegram/Fleet.Telegram.csproj src/Fleet.Telegram/
RUN dotnet restore src/Fleet.Telegram/Fleet.Telegram.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Telegram/Fleet.Telegram.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

EXPOSE 3800

ENTRYPOINT ["dotnet", "Fleet.Telegram.dll"]
