# FrostStream WebAPI.
# Build from the src/ directory:  docker build -f App/WebAPI/Dockerfile -t froststream/webapi .
#
# Backups are executed by the dedicated BackupService image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first so the restore layer caches until dependencies change.
COPY App/Shared/Shared.csproj App/Shared/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/WebAPI/WebAPI.csproj App/WebAPI/
RUN dotnet restore App/WebAPI/WebAPI.csproj

COPY App/Shared/ App/Shared/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/WebAPI/ App/WebAPI/
RUN dotnet publish App/WebAPI/WebAPI.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "WebAPI.dll"]
