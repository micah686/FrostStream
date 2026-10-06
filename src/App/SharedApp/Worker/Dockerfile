# FrostStream Worker (downloads, media fetch, yt-dlp).
# Build from the src/ directory:  docker build -f App/SharedApp/Worker/Dockerfile -t froststream/worker .
#
# The Worker downloads its own yt-dlp/ffmpeg/ffprobe binaries at startup (YtDlpBinaryDownloader),
# so the image only needs CA certificates (present in the base image) and a writable tool
# directory; no python or distro ffmpeg required.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY App/SharedApp/ApplicationContracts/ App/SharedApp/ApplicationContracts/
COPY App/SharedApp/Shared/Shared.csproj App/SharedApp/Shared/
COPY App/SharedApp/StorageExtensions/StorageExtensions.csproj App/SharedApp/StorageExtensions/
COPY App/SharedApp/ServiceDefaults/ServiceDefaults.csproj App/SharedApp/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/SharedApp/Worker/Worker.csproj App/SharedApp/Worker/
RUN dotnet restore App/SharedApp/Worker/Worker.csproj

COPY App/SharedApp/Shared/ App/SharedApp/Shared/
COPY App/SharedApp/StorageExtensions/ App/SharedApp/StorageExtensions/
COPY App/SharedApp/ServiceDefaults/ App/SharedApp/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/SharedApp/Worker/ App/SharedApp/Worker/
RUN dotnet publish App/SharedApp/Worker/Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "Worker.dll"]
