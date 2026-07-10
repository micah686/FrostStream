# FrostStream MediaProcessor.
# Build from the src/ directory:  docker build -f App/MediaProcessor/Dockerfile -t froststream/mediaprocessor .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/MediaProcessor/MediaProcessor.csproj App/MediaProcessor/
RUN dotnet restore App/MediaProcessor/MediaProcessor.csproj

COPY App/Shared/ App/Shared/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/ Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/ Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/MediaProcessor/ App/MediaProcessor/
RUN dotnet publish App/MediaProcessor/MediaProcessor.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "MediaProcessor.dll"]
