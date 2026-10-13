# Build from src/: docker build -f Full/Hosts/BackupService/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY Shared/Contracts/ Shared/Contracts/
COPY Shared/Core/Shared.csproj Shared/Core/
COPY Shared/Integrations/StorageExtensions/StorageExtensions.csproj Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ServiceDefaults.csproj Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Full/Hosts/BackupService/BackupService.csproj Full/Hosts/BackupService/
COPY Full/Infrastructure/ Full/Infrastructure/
RUN dotnet restore Full/Hosts/BackupService/BackupService.csproj

COPY Shared/Core/ Shared/Core/
COPY Shared/Integrations/StorageExtensions/ Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/ Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/ Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Full/Hosts/BackupService/ Full/Hosts/BackupService/
# No --no-restore: this project's Razor components pull in Static Web Assets (blazor.web.js),
# and that pipeline needs a restore pass with the real source present — the earlier csproj-only
# restore (kept for Docker layer caching on the NuGet fetch) leaves its manifest empty otherwise.
RUN dotnet publish Full/Hosts/BackupService/BackupService.csproj -c Release -o /app

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
