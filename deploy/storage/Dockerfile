FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/OneCBackup.Remote/ src/OneCBackup.Remote/
COPY src/OneCBackup.StorageBridge/ src/OneCBackup.StorageBridge/
RUN --mount=type=secret,id=build-ca,required=false \
    set -e; if [ -s /run/secrets/build-ca ]; then cp /run/secrets/build-ca /usr/local/share/ca-certificates/build-network.crt; update-ca-certificates; fi; \
    dotnet publish src/OneCBackup.StorageBridge -c Release -o /out /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12
WORKDIR /app
COPY --from=build /out/ ./
USER app
ENTRYPOINT ["dotnet", "OneCBackup.StorageBridge.dll"]
CMD ["gateway", "/config/gateway.json"]
