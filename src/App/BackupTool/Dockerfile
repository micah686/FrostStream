# FrostStream BackupTool (standalone one-shot image; WebAPI/DataBridge images also embed the
# published tool for in-container backups).
# Build from the src/ directory:  docker build -f App/BackupTool/Dockerfile -t froststream/backuptool .
# Run:  docker run --rm froststream/backuptool <args>
#
# Includes the Postgres client tools (pg_dump/psql/pg_basebackup) the tool shells out to; the
# client major version must be >= the Postgres server major.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY App/BackupTool/BackupTool.csproj App/BackupTool/
RUN dotnet restore App/BackupTool/BackupTool.csproj

COPY App/BackupTool/ App/BackupTool/
RUN dotnet publish App/BackupTool/BackupTool.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "BackupTool.dll"]
