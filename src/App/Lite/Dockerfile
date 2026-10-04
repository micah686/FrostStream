# Build from src/: docker build -f App/Lite/Dockerfile -t froststream/lite .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY App/ApplicationContracts/ App/ApplicationContracts/
COPY App/Shared/ App/Shared/
COPY App/StorageExtensions/ App/StorageExtensions/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/DataBridge/ App/DataBridge/
COPY App/MediaProcessor/ App/MediaProcessor/
COPY App/Scheduler/ App/Scheduler/
COPY App/WebAPI/ App/WebAPI/
COPY App/Worker/ App/Worker/
COPY App/Lite/ App/Lite/
RUN dotnet publish App/Lite/Lite.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Provision the complete runtime toolset at image build time. Startup's SkipExisting
# check finds these files, so a clean installation needs no tool downloads.
ARG YTDLP_VERSION=2026.03.17
ARG DENO_VERSION=2.7.5
# Must match StartPotProvider and YtDlpBinaryDownloaderOptions.
ARG BGUTIL_VERSION=1.3.1
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl unzip ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN set -eu; \
    case "$(dpkg --print-architecture)" in \
      amd64) yt_asset=yt-dlp_linux; deno_target=x86_64-unknown-linux-gnu ;; \
      arm64) yt_asset=yt-dlp_linux_aarch64; deno_target=aarch64-unknown-linux-gnu ;; \
      *) echo 'Lite supports Linux amd64 and arm64' >&2; exit 1 ;; \
    esac; \
    mkdir -p tools/yt-dlp-plugins; \
    curl -fSL --retry 3 "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${yt_asset}" -o "tools/${yt_asset}"; \
    curl -fSL --retry 3 "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-${deno_target}.zip" -o /tmp/deno.zip; \
    unzip /tmp/deno.zip -d tools; \
    curl -fSL --retry 3 "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/${BGUTIL_VERSION}/bgutil-ytdlp-pot-provider.zip" -o /tmp/bgutil.zip; \
    unzip /tmp/bgutil.zip -d tools/yt-dlp-plugins; \
    ln -s /usr/bin/ffmpeg tools/ffmpeg; \
    ln -s /usr/bin/ffprobe tools/ffprobe; \
    chmod +x "tools/${yt_asset}" tools/deno; \
    test -d tools/yt-dlp-plugins/yt_dlp_plugins; \
    "tools/${yt_asset}" --version; tools/deno --version; tools/ffmpeg -version; tools/ffprobe -version; \
    rm /tmp/deno.zip /tmp/bgutil.zip
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    FROSTSTREAM_STORAGE_ROOT=/data
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=60s --retries=5 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "Lite.dll"]
