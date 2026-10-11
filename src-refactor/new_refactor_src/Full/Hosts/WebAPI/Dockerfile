# FrostStream WebAPI.
# Build from the src/ directory:  docker build -f Full/Hosts/WebAPI/Dockerfile -t froststream/webapi .
#
# Backups are executed by the dedicated BackupService image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props ./
# Project files first so the restore layer caches until dependencies change.
COPY Shared/Contracts/ Shared/Contracts/
COPY Shared/Core/Shared.csproj Shared/Core/
COPY Shared/Integrations/StorageExtensions/StorageExtensions.csproj Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ServiceDefaults.csproj Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/Api/WebAPI.csproj Shared/Modules/Api/
COPY Full/Hosts/WebAPI/WebAPIHost.csproj Full/Hosts/WebAPI/
RUN dotnet restore Full/Hosts/WebAPI/WebAPIHost.csproj

COPY Shared/Core/ Shared/Core/
COPY Shared/Integrations/StorageExtensions/ Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/ Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/ Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/Api/ Shared/Modules/Api/
COPY Full/Hosts/WebAPI/ Full/Hosts/WebAPI/
RUN dotnet publish Full/Hosts/WebAPI/WebAPIHost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FrostStream.Full.WebAPI.dll"]
