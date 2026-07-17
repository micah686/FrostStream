# Core Backup And Restore

Backups are executed by the dedicated `backupservice` container. WebAPI submits authenticated admin
requests to its internal HTTP API, while the existing Quartz schedule submits durable NATS jobs. The
container includes PostgreSQL 18 client tools, so neither Aspire nor Compose operators need
`pg_dump`, `pg_restore`, or `pg_basebackup` installed on the host.

FrostStream core backups contain only the data needed to recreate an instance:

- PostgreSQL data — via one of three modes (see below)
- OpenBao KV v2 secret data from the configured mount
- restore requirements and checksums

Media files, local import source files, Typesense data, NATS runtime state, and worker caches are intentionally excluded.

## PostgreSQL Backup Modes

`create --mode <mode>` selects how PostgreSQL is captured. The default is `snapshot`, so existing
callers (the WebAPI admin surface and the DataBridge scheduler) continue to produce logical snapshots
unchanged.

| Mode | Tool | Contents | Use |
| --- | --- | --- | --- |
| `snapshot` (default) | `pg_dump -F c` per database | `postgres/<db>.dump` + OpenBao export | Quick logical snapshot of `froststreamdb`, `authentikdb`, `openfgadb`. |
| `full` | `pg_basebackup -F t -z -X stream` | `postgres/basebackup/` (`base.tar.gz`, `pg_wal.tar.gz`, `backup_manifest`) + OpenBao export | Physical cluster base backup; the base image for point-in-time recovery (PITR). |
| `wal-archive` | server `archive_command` + receiver | initializes an external WAL archive store; emits server settings | Continuous WAL archiving that, combined with a `full` backup, enables PITR. |

### Server prerequisites for `full` and `wal-archive`

Both physical modes require, on the PostgreSQL server:

- `wal_level = replica` (or higher)
- `max_wal_senders >= 1` (default 10 is fine) — for `pg_basebackup` streaming
- a role with the `REPLICATION` privilege (superuser works); pass it via `--postgres-repl-user`
- a `pg_hba.conf` `host replication` rule that permits the backup container; SCRAM authentication
  is recommended
- for continuous archiving: `archive_mode = on` and an `archive_command` (see `wal-archive setup`)

## Create A Backup

The normal path is **Admin → Backups** or `POST /api/global/backups`. Jobs and their state are stored
beneath the backup root, survive service restarts, and only appear in the archive list after the
temporary output has been atomically promoted. Only one backup executes at a time.

For Docker Compose, the same image can be run as a one-shot CLI:

```bash
cd src/App/docker-compose-artifacts
docker compose run --rm --entrypoint dotnet backupservice \
  /app/backuptool/BackupTool.dll create \
  --output /backups/archives \
  --name froststream-core-$(date -u +%Y%m%d%H%M%S)
```

The lower-level host command remains available for development, but requires compatible PostgreSQL
tools on the host:

Snapshot (default — unchanged behavior):

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  create \
  --output /var/backups/froststream \
  --name froststream-core-$(date -u +%Y%m%d%H%M%S) \
  --postgres-host localhost \
  --postgres-port 5432 \
  --postgres-user postgres \
  --openbao-address http://127.0.0.1:8200 \
  --openbao-kv-mount secret
```

Full physical base backup:

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  create --mode full \
  --output /var/backups/froststream \
  --postgres-host localhost --postgres-port 5432 \
  --postgres-repl-user postgres \
  --openbao-address http://127.0.0.1:8200 --openbao-kv-mount secret
```

Set `POSTGRES_PASSWORD` and `OPENBAO_TOKEN` in the environment rather than passing them on the command line.

## Continuous WAL Archiving (PITR)

`wal-archive setup` prints the PostgreSQL settings to apply. `--tool-command` is how the server should
invoke this tool (use an absolute path to a published binary, not `dotnet run`):

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  wal-archive setup --archive-dir /var/backups/froststream/wal-archive \
  --tool-command /opt/froststream/BackupTool
```

That emits, for `postgresql.conf`:

```
wal_level = replica
archive_mode = on
archive_command = '/opt/froststream/BackupTool wal-archive receive %p %f --archive-dir /var/backups/froststream/wal-archive'
max_wal_senders = 10
```

`create --mode wal-archive --archive-dir <dir>` initializes the archive store and records it in the
backup manifest. PostgreSQL then streams each completed segment into `<dir>` via `wal-archive receive`.
`wal-archive receive`/`restore` are invoked by the server, not by operators.

## Verify A Backup

From Compose, use the bundled tools:

```bash
docker compose run --rm --entrypoint dotnet backupservice \
  /app/backuptool/BackupTool.dll verify \
  --archive /backups/archives/<backup-name>
```

The Admin UI invokes the same verification through `backupservice`.

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  verify \
  --archive /var/backups/froststream/froststream-core-20260628010203
```

`verify` always checks the archive-wide SHA-256 checksums, then runs a mode-specific structural
check: `pg_restore --list` on each dump (`snapshot`), `pg_verifybackup` on the base backup (`full`),
or WAL segment checksum + continuity checks (`wal-archive`).

## Restore

Restore is a cold/offline operation.

### Snapshot restore (logical)

1. Stop WebAPI, DataBridge, Worker, Scheduler, Authentik, OpenFGA, and BackupService. Keep PostgreSQL
   and OpenBao running and reachable.
2. Run the one-shot restore container:

```bash
docker compose stop webapi databridge worker scheduler authentik authentik-worker openfga
docker compose run --rm --entrypoint dotnet backupservice \
  /app/backuptool/BackupTool.dll restore \
  --archive /backups/archives/<backup-name> --force
docker compose up -d
```

The equivalent host command is:

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  restore \
  --archive /var/backups/froststream/froststream-core-20260628010203 \
  --force
```

Each database is dropped, recreated, and restored with `pg_restore --clean --if-exists`; OpenBao
secrets are re-applied.

### Full / point-in-time restore (physical)

A full restore rebuilds a PostgreSQL data directory, so the server must be stopped and its data
directory is replaced. Provide `--pgdata` (and optionally `--pg-ctl`) so the tool stops the server,
clears the data directory, extracts `base.tar.gz` and `pg_wal.tar.gz`, and restarts it:

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  restore --force \
  --archive /var/backups/froststream/froststream-full-20260628010203 \
  --pgdata /var/lib/postgresql/data --pg-ctl /usr/lib/postgresql/18/bin/pg_ctl
```

For point-in-time recovery, add a recovery target and the WAL archive directory. The tool writes
`recovery.signal` and a `restore_command` (pointing back at `wal-archive restore`) into
`postgresql.auto.conf`, then starts the server so it replays WAL to the target and promotes:

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  restore --force \
  --archive /var/backups/froststream/froststream-full-20260628010203 \
  --pgdata /var/lib/postgresql/data --pg-ctl /usr/lib/postgresql/18/bin/pg_ctl \
  --archive-dir /var/backups/froststream/wal-archive \
  --target-time '2026-06-28 02:05:00+00' \
  --tool-command /opt/froststream/BackupTool
```

Use `--target-lsn`, `--target-name`, or `--recover-latest` instead of `--target-time` as needed. If
`--pgdata` is omitted, the tool prints the offline steps instead of making changes.

Finally:

1. Restart services.
2. Trigger a metadata search reindex so Typesense is rebuilt from PostgreSQL.

## WebAPI Admin Surface

WebAPI proxies these authenticated management endpoints to BackupService:

- `POST /api/global/backups`
- `GET /api/global/backups`
- `GET /api/global/backups/jobs`
- `GET /api/global/backups/jobs/{jobId}`
- `POST /api/global/backups/verify`
- `POST /api/global/backups/restore-plan`

`POST /api/global/backups` accepts an optional `mode` (`snapshot` \| `full` \| `wal-archive`,
default `snapshot`), selectable from the **Admin → Backups** panel. Each archive's mode is shown in
the list, and `restore-plan` tailors the offline command to the backup's mode (full/PITR restores
include `--pgdata`/`--pg-ctl` and a recovery-target placeholder).

The API can start and verify backups, but it does not perform restore. `restore-plan` returns the
offline CLI command operators should run after stopping services — physical restores are always an
operator action on the database host.

## AppHost / Aspire Configuration

The AppHost wires the dedicated service and physical-backup prerequisites automatically:

- `FROSTSTREAM_BACKUP_ROOT` controls the host bind mounted at `/backups` in BackupService. It
  defaults to `<storage-root>/core-backups` in Aspire and `./backups` beside the generated Compose
  file. Completed archives, durable job records, and WAL live in separate subdirectories.
- `src/App/AppHost/configs/postgres/postgresql.conf` is mounted into the Postgres container
  (`-c config_file=…`) and sets `wal_level=replica`, `max_wal_senders`, `archive_mode=on`, and an
  `archive_command` that copies each completed segment — with a matching `<segment>.sha256` sidecar —
  into a shared `/wal-archive` bind mount (`<storage-root>/wal-archive` on the host).
- `src/App/AppHost/configs/postgres/pg_hba.conf` is also mounted and permits normal and replication
  connections from the private container network using SCRAM password authentication. The explicit
  replication rule is required because a normal `host all all` rule does not match the replication
  protocol used by `pg_basebackup`.
- The `archive_command` `chmod 0644`s the archived files so the BackupTool process (which runs as the
  host user, not the container's postgres user under rootless podman) can read them.
- Only BackupService receives PostgreSQL/OpenBao backup credentials and the archive directory.
  WebAPI proxies the management API; DataBridge no longer contains PostgreSQL client tooling.

Because the containerized Postgres invokes its own `archive_command` (a shell `cp` + `sha256sum`), it
produces the same on-disk format as `BackupTool wal-archive receive`. On a bare-metal server where the
tool is on PATH, you can instead point `archive_command` directly at `wal-archive receive` (see
`wal-archive setup`).

## First Compose Start: OpenBao

OpenBao uses a persistent single-node Raft volume instead of ephemeral `-dev` mode. On a new Compose
deployment, initialize and unseal it before starting the application:

```bash
cd src/App/docker-compose-artifacts
mkdir -p backups/wal
docker compose up -d openbao
docker compose exec openbao bao operator init
docker compose exec openbao bao operator unseal
docker compose exec openbao sh
```

Save the unseal keys and initial root token in a secure system outside this host. At the interactive
container shell, enter the root token without placing it in shell history, then provision the mount
and app token configured in `.env`:

```sh
read -s BAO_TOKEN; export BAO_TOKEN
bao secrets enable -path=secret kv-v2
bao token create -id="$OPENBAO_APP_TOKEN" -policy=root -no-default-policy
exit
```

Then run `docker compose up -d`. On every later OpenBao restart, run
`docker compose exec openbao bao operator unseal` before dependent services become healthy. Aspire
development automates one-share initialization and unseal using owner-readable files under
`<storage-root>/openbao-bootstrap`; this convenience workflow is not emitted into Compose.

The application token retains the current root-level behavior for compatibility. Replacing it with
least-privilege policies/AppRole and configuring an external auto-unseal provider are separate
production-hardening tasks.

## Scheduled Retention

The seeded `nightly-backup` schedule remains disabled and runs at 02:00 UTC when enabled. After each
successful scheduled snapshot, BackupService retains the newest 14 scheduled snapshots. Manual,
full, WAL, failed-diagnostic, and active archives are never automatically removed.
