FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY src/Fleet.Temporal/Fleet.Temporal.csproj src/Fleet.Temporal/
RUN dotnet restore src/Fleet.Temporal/Fleet.Temporal.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Temporal/Fleet.Temporal.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app

RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

EXPOSE 3001

ENTRYPOINT ["dotnet", "Fleet.Temporal.dll"]
