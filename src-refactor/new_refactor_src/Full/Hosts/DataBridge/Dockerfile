# FrostStream DataBridge (persistence, migrations, download orchestration).
# Build from the src/ directory:  docker build -f Full/Hosts/DataBridge/Dockerfile -t froststream/databridge .
#
# Scheduled backups run in the dedicated BackupService container.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props ./
COPY Shared/Contracts/ Shared/Contracts/
COPY Shared/Core/Shared.csproj Shared/Core/
COPY Shared/Integrations/StorageExtensions/StorageExtensions.csproj Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ServiceDefaults.csproj Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/DataBridge/DataBridge.csproj Shared/Modules/DataBridge/
COPY Full/Hosts/DataBridge/DataBridgeHost.csproj Full/Hosts/DataBridge/
COPY Full/Infrastructure/ Full/Infrastructure/
COPY Shared/Persistence/ Shared/Persistence/
COPY Shared/Integrations/Typesense/ Shared/Integrations/Typesense/
RUN dotnet restore Full/Hosts/DataBridge/DataBridgeHost.csproj

COPY Shared/Core/ Shared/Core/
COPY Shared/Integrations/StorageExtensions/ Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/ Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/ Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/DataBridge/ Shared/Modules/DataBridge/
COPY Full/Hosts/DataBridge/ Full/Hosts/DataBridge/
RUN dotnet publish Full/Hosts/DataBridge/DataBridgeHost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "FrostStream.Full.DataBridge.dll"]
