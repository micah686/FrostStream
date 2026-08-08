# FrostStream MediaProcessor.
# Build from the src/ directory:  docker build -f App/MediaProcessor/Dockerfile -t froststream/mediaprocessor .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/StorageExtensions/StorageExtensions.csproj App/StorageExtensions/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/MediaProcessor/MediaProcessor.csproj App/MediaProcessor/
RUN dotnet restore App/MediaProcessor/MediaProcessor.csproj

COPY App/Shared/ App/Shared/
COPY App/StorageExtensions/ App/StorageExtensions/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/MediaProcessor/ App/MediaProcessor/
RUN dotnet publish App/MediaProcessor/MediaProcessor.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ffmpeg/ffprobe drive every rendition encode.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "MediaProcessor.dll"]
