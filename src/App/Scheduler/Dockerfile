# FrostStream Scheduler (Quartz; serves the /quartz dashboard).
# Build from the src/ directory:  docker build -f App/Scheduler/Dockerfile -t froststream/scheduler .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/StorageExtensions/StorageExtensions.csproj App/StorageExtensions/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/Scheduler/Scheduler.csproj App/Scheduler/
RUN dotnet restore App/Scheduler/Scheduler.csproj

COPY App/Shared/ App/Shared/
COPY App/StorageExtensions/ App/StorageExtensions/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/Scheduler/ App/Scheduler/
RUN dotnet publish App/Scheduler/Scheduler.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Scheduler.dll"]
