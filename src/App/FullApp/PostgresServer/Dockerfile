# FrostStream PostgreSQL server image: stock postgres + pgBackRest so archive_command can
# push WAL segments into the shared repo (see FullApp/AppHostFull/configs/pgbackrest/pgbackrest.conf).
# Build from src/: docker build -f App/FullApp/PostgresServer/Dockerfile .
FROM docker.io/library/postgres:18.3
RUN apt-get update \
    && apt-get install -y --no-install-recommends pgbackrest \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/log/pgbackrest \
    && chown postgres:postgres /var/log/pgbackrest
