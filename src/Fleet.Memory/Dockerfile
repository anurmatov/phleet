FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY src/Fleet.Memory/Fleet.Memory.csproj src/Fleet.Memory/
RUN dotnet restore src/Fleet.Memory/Fleet.Memory.csproj
COPY src/ src/
RUN dotnet publish src/Fleet.Memory/Fleet.Memory.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app

RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Bake ONNX model + vocab into image (downloaded by scripts/download-onnx-model.sh before build)
COPY models/fleet-memory/ /app/models/

RUN mkdir -p /data/memories

EXPOSE 3100

ENTRYPOINT ["dotnet", "Fleet.Memory.dll"]
