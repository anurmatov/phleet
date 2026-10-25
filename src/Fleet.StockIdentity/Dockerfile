FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/Fleet.StockIdentity/Fleet.StockIdentity.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY src/Fleet.StockIdentity/Proto/LICENSE /app/THIRD_PARTY_OPENSHELL_LICENSE
USER $APP_UID
# Authority, TLS serving keys and gateway trust are operator-owned read-only inputs.
# No workload credential or private signer is baked into this artifact.
ENTRYPOINT ["dotnet", "Fleet.StockIdentity.dll"]
