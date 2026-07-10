# FrostStream Worker (downloads, media fetch, yt-dlp).
# Build from the src/ directory:  docker build -f App/Worker/Dockerfile -t froststream/worker .
#
# The Worker downloads its own yt-dlp/ffmpeg/ffprobe binaries at startup (YtDlpBinaryDownloader),
# so the image only needs CA certificates (present in the base image) and a writable tool
# directory; no python or distro ffmpeg required.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/Worker/Worker.csproj App/Worker/
RUN dotnet restore App/Worker/Worker.csproj

COPY App/Shared/ App/Shared/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/ Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/ Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/Worker/ App/Worker/
RUN dotnet publish App/Worker/Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "Worker.dll"]
