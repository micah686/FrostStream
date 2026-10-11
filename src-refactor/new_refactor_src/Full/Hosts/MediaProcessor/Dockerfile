# FrostStream MediaProcessor.
# Build from the src/ directory:  docker build -f Full/Hosts/MediaProcessor/Dockerfile -t froststream/mediaprocessor .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props ./
COPY Shared/Contracts/ Shared/Contracts/
COPY Shared/Core/Shared.csproj Shared/Core/
COPY Shared/Integrations/StorageExtensions/StorageExtensions.csproj Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ServiceDefaults.csproj Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/MediaProcessor/MediaProcessor.csproj Shared/Modules/MediaProcessor/
COPY Full/Hosts/MediaProcessor/MediaProcessorHost.csproj Full/Hosts/MediaProcessor/
RUN dotnet restore Full/Hosts/MediaProcessor/MediaProcessorHost.csproj

COPY Shared/Core/ Shared/Core/
COPY Shared/Integrations/StorageExtensions/ Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/ Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/ Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/MediaProcessor/ Shared/Modules/MediaProcessor/
COPY Full/Hosts/MediaProcessor/ Full/Hosts/MediaProcessor/
RUN dotnet publish Full/Hosts/MediaProcessor/MediaProcessorHost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ffmpeg/ffprobe drive every rendition encode.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "FrostStream.Full.MediaProcessor.dll"]
