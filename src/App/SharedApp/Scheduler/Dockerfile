# FrostStream Scheduler (Quartz; serves the /quartz dashboard).
# Build from the src/ directory:  docker build -f App/SharedApp/Scheduler/Dockerfile -t froststream/scheduler .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY App/SharedApp/ApplicationContracts/ App/SharedApp/ApplicationContracts/
COPY App/SharedApp/Shared/Shared.csproj App/SharedApp/Shared/
COPY App/SharedApp/StorageExtensions/StorageExtensions.csproj App/SharedApp/StorageExtensions/
COPY App/SharedApp/ServiceDefaults/ServiceDefaults.csproj App/SharedApp/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/SharedApp/Scheduler/Scheduler.csproj App/SharedApp/Scheduler/
RUN dotnet restore App/SharedApp/Scheduler/Scheduler.csproj

COPY App/SharedApp/Shared/ App/SharedApp/Shared/
COPY App/SharedApp/StorageExtensions/ App/SharedApp/StorageExtensions/
COPY App/SharedApp/ServiceDefaults/ App/SharedApp/ServiceDefaults/
COPY Libs/Conduit.NATS/Conduit.NATS/ Libs/Conduit.NATS/Conduit.NATS/
COPY Libs/YtDlpSharpLib/YtDlpSharpLib/ Libs/YtDlpSharpLib/YtDlpSharpLib/
COPY App/SharedApp/Scheduler/ App/SharedApp/Scheduler/
RUN dotnet publish App/SharedApp/Scheduler/Scheduler.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Scheduler.dll"]
