# Build from src/: docker build -f App/BackupService/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/BackupService/BackupService.csproj App/BackupService/
COPY App/BackupTool/BackupTool.csproj App/BackupTool/
RUN dotnet restore App/BackupService/BackupService.csproj && dotnet restore App/BackupTool/BackupTool.csproj

COPY App/Shared/ App/Shared/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/ Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/ Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/BackupService/ App/BackupService/
COPY App/BackupTool/ App/BackupTool/
RUN dotnet publish App/BackupService/BackupService.csproj -c Release -o /app --no-restore \
    && dotnet publish App/BackupTool/BackupTool.csproj -c Release -o /app/backuptool --no-restore

# Using the matching PostgreSQL image guarantees pg_dump/pg_restore/pg_basebackup are compatible
# with the server. Copy the ASP.NET runtime into that Debian image rather than relying on the
# distribution's potentially older postgresql-client package.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS dotnet-runtime
FROM docker.io/library/postgres:18.3
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
COPY --from=build /app /app
ENV PATH="/usr/share/dotnet:${PATH}" \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Backup__ToolPath=dotnet \
    Backup__ToolBaseArguments=/app/backuptool/BackupTool.dll
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["dotnet", "BackupService.dll"]
