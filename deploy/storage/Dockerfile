FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG VERSION=0.1.0-dev
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/OneCBackup.Remote/ src/OneCBackup.Remote/
COPY src/OneCBackup.StorageBridge/ src/OneCBackup.StorageBridge/
RUN --mount=type=secret,id=build-ca,required=false \
    set -e; if [ -s /run/secrets/build-ca ]; then cp /run/secrets/build-ca /usr/local/share/ca-certificates/build-network.crt; update-ca-certificates; fi; \
    dotnet publish src/OneCBackup.StorageBridge -c Release -o /out /p:UseAppHost=false /p:Version=$VERSION
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
ARG VERSION=0.1.0-dev
LABEL org.opencontainers.image.version=$VERSION
LABEL org.opencontainers.image.source="https://github.com/sanekman/1C-Backup"
WORKDIR /app
COPY --from=build /out/ ./
USER app
ENTRYPOINT ["dotnet", "OneCBackup.StorageBridge.dll"]
CMD ["gateway", "/config/gateway.json"]
