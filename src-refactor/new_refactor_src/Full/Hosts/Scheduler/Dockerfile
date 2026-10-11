# FrostStream Scheduler (Quartz; serves the /quartz dashboard).
# Build from the src/ directory:  docker build -f Full/Hosts/Scheduler/Dockerfile -t froststream/scheduler .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props ./
COPY Shared/Contracts/ Shared/Contracts/
COPY Shared/Core/Shared.csproj Shared/Core/
COPY Shared/Integrations/StorageExtensions/StorageExtensions.csproj Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ServiceDefaults.csproj Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/Scheduler/Scheduler.csproj Shared/Modules/Scheduler/
COPY Full/Hosts/Scheduler/SchedulerHost.csproj Full/Hosts/Scheduler/
COPY Full/Infrastructure/ Full/Infrastructure/
COPY Shared/Persistence/ Shared/Persistence/
RUN dotnet restore Full/Hosts/Scheduler/SchedulerHost.csproj

COPY Shared/Core/ Shared/Core/
COPY Shared/Integrations/StorageExtensions/ Shared/Integrations/StorageExtensions/
COPY Shared/Hosting/ServiceDefaults/ Shared/Hosting/ServiceDefaults/
COPY Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/ Shared/Integrations/Vendor/Conduit.NATS/Conduit.NATS/
COPY Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/ Shared/Integrations/Vendor/YtDlpSharpLib/YtDlpSharpLib/
COPY Shared/Modules/Scheduler/ Shared/Modules/Scheduler/
COPY Full/Hosts/Scheduler/ Full/Hosts/Scheduler/
RUN dotnet publish Full/Hosts/Scheduler/SchedulerHost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FrostStream.Full.Scheduler.dll"]
