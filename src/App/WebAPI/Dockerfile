# FrostStream WebAPI.
# Build from the src/ directory:  docker build -f App/WebAPI/Dockerfile -t froststream/webapi .
#
# Also bundles the published BackupTool and the Postgres client tools (pg_dump/psql/pg_basebackup)
# so on-demand backups work in containers; Backup__ToolPath is preset accordingly. The pg client
# major version must be >= the Postgres server major.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first so the restore layer caches until dependencies change.
COPY App/Shared/Shared.csproj App/Shared/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/WebAPI/WebAPI.csproj App/WebAPI/
COPY App/BackupTool/BackupTool.csproj App/BackupTool/
RUN dotnet restore App/WebAPI/WebAPI.csproj && dotnet restore App/BackupTool/BackupTool.csproj

COPY App/Shared/ App/Shared/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/ Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/ Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/WebAPI/ App/WebAPI/
COPY App/BackupTool/ App/BackupTool/
RUN dotnet publish App/WebAPI/WebAPI.csproj -c Release -o /app --no-restore \
    && dotnet publish App/BackupTool/BackupTool.csproj -c Release -o /app/backuptool --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Backup__ToolPath=dotnet \
    Backup__ToolBaseArguments=/app/backuptool/BackupTool.dll
EXPOSE 8080

ENTRYPOINT ["dotnet", "WebAPI.dll"]
