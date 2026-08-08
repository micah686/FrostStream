# Build from src/: docker build -f App/BackupService/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/StorageExtensions/StorageExtensions.csproj App/StorageExtensions/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/BackupService/BackupService.csproj App/BackupService/
RUN dotnet restore App/BackupService/BackupService.csproj

COPY App/Shared/ App/Shared/
COPY App/StorageExtensions/ App/StorageExtensions/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/BackupService/ App/BackupService/
# No --no-restore: this project's Razor components pull in Static Web Assets (blazor.web.js),
# and that pipeline needs a restore pass with the real source present — the earlier csproj-only
# restore (kept for Docker layer caching on the NuGet fetch) leaves its manifest empty otherwise.
RUN dotnet publish App/BackupService/BackupService.csproj -c Release -o /app

# Base on the matching PostgreSQL image so pgbackrest, pg_ctl, psql, and pg_isready are
# version-matched with the server (deep verify starts a throwaway postgres from the restored
# files). Copy the ASP.NET runtime into that Debian image rather than relying on the
# distribution's potentially older packages.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS dotnet-runtime
FROM docker.io/library/postgres:18.3
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl pgbackrest \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/log/pgbackrest \
    && chown postgres:postgres /var/log/pgbackrest
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
COPY --from=build /app /app
ENV PATH="/usr/share/dotnet:${PATH}" \
    ASPNETCORE_URLS="http://0.0.0.0:8080;http://0.0.0.0:8081"
WORKDIR /app
EXPOSE 8080 8081
# Run as the postgres uid (999, identical in the postgres server image) so everything
# pgBackRest writes — the repo under /backups and restored cluster files in the shared data
# volume — is natively owned by the user the postgres container runs as.
USER postgres
ENTRYPOINT ["dotnet", "BackupService.dll"]
