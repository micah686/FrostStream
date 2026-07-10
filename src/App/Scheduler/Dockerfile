# FrostStream Scheduler (Quartz; serves the /quartz dashboard).
# Build from the src/ directory:  docker build -f App/Scheduler/Dockerfile -t froststream/scheduler .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/Shared/Shared.csproj App/Shared/
COPY App/ServiceDefaults/ServiceDefaults.csproj App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/Conduit.NATS.csproj Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/YtDlpSharpLib.csproj Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/Scheduler/Scheduler.csproj App/Scheduler/
RUN dotnet restore App/Scheduler/Scheduler.csproj

COPY App/Shared/ App/Shared/
COPY App/ServiceDefaults/ App/ServiceDefaults/
COPY Modules/Conduit.NATS/Conduit.NATS/ Modules/Conduit.NATS/Conduit.NATS/
COPY Modules/YtDlpSharpLib/YtDlpSharpLib/ Modules/YtDlpSharpLib/YtDlpSharpLib/
COPY App/Scheduler/ App/Scheduler/
RUN dotnet publish App/Scheduler/Scheduler.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Scheduler.dll"]
