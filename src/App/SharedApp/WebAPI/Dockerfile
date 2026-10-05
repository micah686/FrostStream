# FrostStream WebAPI.
# Build from the src/ directory:  docker build -f App/SharedApp/WebAPI/Dockerfile -t froststream/webapi .
#
# Backups are executed by the dedicated BackupService image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
# Project files first so the restore layer caches until dependencies change.
COPY App/SharedApp/ApplicationContracts/ App/SharedApp/ApplicationContracts/
COPY App/SharedApp/Shared/Shared.csproj App/SharedApp/Shared/
COPY App/SharedApp/StorageExtensions/StorageExtensions.csproj App/SharedApp/StorageExtensions/
COPY App/SharedApp/ServiceDefaults/ServiceDefaults.csproj App/SharedApp/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/SharedApp/WebAPI/WebAPI.csproj App/SharedApp/WebAPI/
RUN dotnet restore App/SharedApp/WebAPI/WebAPI.csproj

COPY App/SharedApp/Shared/ App/SharedApp/Shared/
COPY App/SharedApp/StorageExtensions/ App/SharedApp/StorageExtensions/
COPY App/SharedApp/ServiceDefaults/ App/SharedApp/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/SharedApp/WebAPI/ App/SharedApp/WebAPI/
RUN dotnet publish App/SharedApp/WebAPI/WebAPI.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "WebAPI.dll"]
