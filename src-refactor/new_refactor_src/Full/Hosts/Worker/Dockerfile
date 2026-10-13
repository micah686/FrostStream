# FrostStream Worker (downloads, media fetch, yt-dlp).
# Build from the src/ directory:  docker build -f Full/Hosts/Worker/Dockerfile -t froststream/worker .
#
# The Worker downloads its own yt-dlp/ffmpeg/ffprobe binaries at startup (YtDlpBinaryDownloader),
# so the image only needs CA certificates (present in the base image) and a writable tool
# directory; no python or distro ffmpeg required.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props ./
COPY Shared/Contracts/ Shared/Contracts/
COPY Shared/Core/Shared.csproj Shared/Core/
COPY Shared/Integrations/StorageExtensions/StorageExtensions.csproj Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ServiceDefaults.csproj Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/Worker/Worker.csproj Shared/Modules/Worker/
COPY Full/Hosts/Worker/WorkerHost.csproj Full/Hosts/Worker/
COPY Full/Infrastructure/ Full/Infrastructure/
COPY Shared/Persistence/ Shared/Persistence/
RUN dotnet restore Full/Hosts/Worker/WorkerHost.csproj

COPY Shared/Core/ Shared/Core/
COPY Shared/Integrations/StorageExtensions/ Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/ Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/ Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/Worker/ Shared/Modules/Worker/
COPY Full/Hosts/Worker/ Full/Hosts/Worker/
RUN dotnet publish Full/Hosts/Worker/WorkerHost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "FrostStream.Full.Worker.dll"]
