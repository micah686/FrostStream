# FrostStream DataBridge (persistence, migrations, download orchestration).
# Build from the src/ directory:  docker build -f App/DataBridge/Dockerfile -t froststream/databridge .
#
# Scheduled backups run in DataBridge, so like the WebAPI image it bundles the published
# BackupTool and the Postgres client tools; Backup__ToolPath is preset accordingly.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/DataBridge/DataBridge.csproj App/DataBridge/
COPY App/BackupTool/BackupTool.csproj App/BackupTool/
RUN dotnet restore App/DataBridge/DataBridge.csproj && dotnet restore App/BackupTool/BackupTool.csproj

COPY App/Shared/ App/Shared/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/ Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/ Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/DataBridge/ App/DataBridge/
COPY App/BackupTool/ App/BackupTool/
RUN dotnet publish App/DataBridge/DataBridge.csproj -c Release -o /app --no-restore \
    && dotnet publish App/BackupTool/BackupTool.csproj -c Release -o /app/backuptool --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENV Backup__ToolPath=dotnet \
    Backup__ToolBaseArguments=/app/backuptool/BackupTool.dll

ENTRYPOINT ["dotnet", "DataBridge.dll"]
