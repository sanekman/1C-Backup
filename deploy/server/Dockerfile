FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/SPBackup.Server/ src/SPBackup.Server/
COPY src/SPBackup.Core/ServerMonitoringContracts.cs src/SPBackup.Core/ServerMonitoringContracts.cs
RUN --mount=type=secret,id=build-ca,required=false \
    set -e; if [ -s /run/secrets/build-ca ]; then cp /run/secrets/build-ca /usr/local/share/ca-certificates/build-network.crt; update-ca-certificates; fi; \
    dotnet restore src/SPBackup.Server --locked-mode
RUN dotnet publish src/SPBackup.Server -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
WORKDIR /app
COPY --from=build /out/ ./
RUN chmod -R a+rX /app && mkdir -p /var/lib/onec-backup-server && chown app:app /var/lib/onec-backup-server && chmod 700 /var/lib/onec-backup-server
USER app
ENV ASPNETCORE_URLS=https://+:7443 Server__DataDirectory=/var/lib/onec-backup-server
EXPOSE 7443
ENTRYPOINT ["dotnet", "SPBackup.Server.dll"]
