# Build from src/: docker build -f Lite/Backend/Dockerfile -t froststream/lite .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props Directory.Build.props ./
COPY Shared/Contracts/ Shared/Contracts/
COPY Shared/Core/ Shared/Core/
COPY Shared/Integrations/StorageExtensions/ Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/ Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/DataBridge/ Shared/Modules/DataBridge/
COPY Shared/Modules/MediaProcessor/ Shared/Modules/MediaProcessor/
COPY Shared/Modules/Scheduler/ Shared/Modules/Scheduler/
COPY Shared/Modules/Api/ Shared/Modules/Api/
COPY Shared/Modules/Worker/ Shared/Modules/Worker/
COPY Shared/Persistence/ Shared/Persistence/
COPY Lite/Infrastructure/ Lite/Infrastructure/
COPY Lite/Backend/ Lite/Backend/
RUN dotnet publish Lite/Backend/Lite.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Provision the complete runtime toolset at image build time. Startup's SkipExisting
# check finds these files before the scheduled refresh checks for newer releases.
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
    curl -fSL --retry 3 "https://github.com/yt-dlp/yt-dlp/releases/latest/download/${yt_asset}" -o "tools/${yt_asset}"; \
    deno_version="$(curl -fsSL --retry 3 https://dl.deno.land/release-latest.txt)"; \
    curl -fSL --retry 3 "https://dl.deno.land/release/${deno_version}/deno-${deno_target}.zip" -o /tmp/deno.zip; \
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
