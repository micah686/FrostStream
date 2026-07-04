# Core Backup And Restore

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
- for continuous archiving: `archive_mode = on` and an `archive_command` (see `wal-archive setup`)

## Create A Backup

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

1. Stop WebAPI, DataBridge, Worker, Scheduler, Authentik, OpenFGA, and OpenBao.
2. Ensure PostgreSQL and OpenBao are reachable.
3. Run:

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

When `Backup` options are configured, WebAPI exposes:

- `POST /api/admin/backups`
- `GET /api/admin/backups`
- `GET /api/admin/backups/jobs`
- `GET /api/admin/backups/jobs/{jobId}`
- `POST /api/admin/backups/verify`
- `POST /api/admin/backups/restore-plan`

The API can start and verify backups, but it does not perform restore. `restore-plan` returns the offline CLI command operators should run after stopping services.

The admin surface currently drives only the default `snapshot` mode. `full` and `wal-archive` are CLI-only for now.
