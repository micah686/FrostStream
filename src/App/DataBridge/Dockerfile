# FrostStream DataBridge (persistence, migrations, download orchestration).
# Build from the src/ directory:  docker build -f App/DataBridge/Dockerfile -t froststream/databridge .
#
# Scheduled backups run in the dedicated BackupService container.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/StorageExtensions/StorageExtensions.csproj App/StorageExtensions/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/DataBridge/DataBridge.csproj App/DataBridge/
RUN dotnet restore App/DataBridge/DataBridge.csproj

COPY App/Shared/ App/Shared/
COPY App/StorageExtensions/ App/StorageExtensions/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/DataBridge/ App/DataBridge/
RUN dotnet publish App/DataBridge/DataBridge.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "DataBridge.dll"]
