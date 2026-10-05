# Core Backup And Restore

## Lite: SQLite snapshots and recovery

Lite uses the shared **Admin → Backups** page and backup API with an embedded SQLite adapter.
It supports full snapshots and verification; differential backups, deep PostgreSQL restore verification,
and PITR are unavailable. Recovery is a **server-shutdown and file-replacement procedure**.
There is no Lite online restore endpoint or standalone PostgreSQL restore console.

### Locations and recovery artifacts

The page shows resolved paths; relative configuration paths resolve against the Lite host content root.

| Artifact | Setting | Default |
| --- | --- | --- |
| Live application database | `Persistence:Sqlite:Path` | `/data/frostreamlitedb` |
| Live secret encryption key directory | `Secrets:Local:KeyRingPath` | `<resolved database path>.keys` |
| Backup directory | `Backup:Directory` (fallback `Persistence:Sqlite:BackupPath`) | `/data/backups` |

Each newly completed snapshot is a **three-part recovery set**:

- `<timestamp>-<id>.sqlite`: the standalone database, made through SQLite's online backup API.
- `<timestamp>-<id>.sqlite.keys/`: the **entire** local Data Protection key ring, including older
  and revoked key records and any other persisted ring files. Keep all of it, even after rotation.
- `<timestamp>-<id>.sqlite.recovery.json`: versioned SHA-256 checksums of the database and every
  companion key file. These detect missing or changed files; they do not authenticate an untrusted backup.

The adapter copies keys **after** capturing the database, then checks database integrity, foreign keys,
checksums, and decryption of every snapshotted secret using the companion ring. Verification uses a
read-only database connection and disables automatic key generation. The database is published last,
so incomplete sets do not appear as completed snapshots. On Unix, companion directories have mode
`0700` and files have mode `0600`; on Windows, restrict the backup directory ACL to the server/operator
account. Keep the recovery set in protected storage: its keys permit decryption of the encrypted secrets.
Copy all three artifacts together to off-host storage. The live key ring remains independent of the
WebAPI cookie/token key ring, which is not needed to recover local secrets.

Snapshots include application data, Cleipnir workflow state, durable queue/inbox state, staged import
objects, and encrypted secrets. **Media files, local import source files, optional ClickHouse data,
and Typesense indexes are not in the snapshot.** Preserve media/import sources separately and rebuild
Typesense from restored application data. The existing full-backup schedule works in Lite; differential
schedule definitions are skipped. Job history currently lasts for the server session; published recovery
sets survive restarts.

### Restore procedure

1. Choose a snapshot and use its **Verify** action (or
   `POST /api/global/backups/verify` with `{ "label": "<filename>.sqlite", "deep": false }`).
   A successful completed job confirms database integrity and secret recovery with the companion keys.
   Preserve the original recovery set; perform restoration from copies. A missing companion/manifest
   or verification failure returns a clear error and leaves the live database and keys untouched.
   Older database-only snapshots are flagged in the page and cannot pass this recovery verification.
   If still running the original installation, create a new complete snapshot before moving it.
2. **Shut down the Lite server and every process using the database.** Confirm that shutdown has
   completed before copying/moving the live database, keys, or WAL/SHM files. Do not manipulate
   SQLite sidecars while the server is running.
3. Preserve the current database, its `-wal` and `-shm` files if present, and the complete current
   key directory in a separate, owner-only rollback directory. Record that directory's full path.
   A key directory configured outside `/data` must be preserved as well. Keep this copy until all
   post-restore validation succeeds.
4. Only after shutdown, move any leftover live WAL/SHM files into that rollback directory.
   Copy the selected snapshot to the **resolved live database location**, naming it `frostreamlitedb`
   for the default configuration. Copy the snapshot's entire companion ring to the **resolved live
   key directory**. On a different installation, install these keys before starting the server; newly
   generated keys cannot decrypt the old secrets. Preserve the original key ring in the rollback copy
   rather than deleting it or copying only the newest key. Ensure the server account owns and can
   read/write the restored database and keys; retain owner-only key-directory permissions.
5. Restart with the same deployment configuration and a schema-compatible application version.
   Check `/health`, library data, schedules, import manifests, and workflows. Exercise a storage or
   notification operation that reads saved credentials to confirm secret decryption. Rebuild the
   Typesense index. Restored queues/workflows follow normal startup reconciliation: queued downloads
   stop and active runs become interrupted/failed; explicit retry is required, and stale deliveries
   must not restart invalidated runs. Restoring state does not roll back external media-file effects.
6. If any validation fails, **stop the server again**. Record/report the rollback directory, keep it
   intact, and preserve the failed restored database, sidecars, and keys in a different directory for
   diagnosis. Put the saved database and complete key ring back at their original locations. Keep
   any preserved sidecars with the database they belong to; never apply the old WAL to the selected
   snapshot. Restart and verify the previous installation before retrying recovery. Do not overwrite
   the only known recoverable copy.

Example file replacement on Unix **after shutdown**, run as the server's storage owner (adjust paths
for mounts/custom settings; `$LITE_ROLLBACK` must be a new directory outside the live key ring):

```bash
(
set -eu
LITE_DB=/data/frostreamlitedb
LITE_KEYS=/data/frostreamlitedb.keys
LITE_SNAPSHOT='/data/backups/<timestamp>-<id>.sqlite'
LITE_ROLLBACK=/data/rollback-$(date -u +%Y%m%dT%H%M%SZ)
mkdir -m 700 -- "$LITE_ROLLBACK"
mv -- "$LITE_DB" "$LITE_ROLLBACK/database"
for suffix in -wal -shm; do
  if [ -f "$LITE_DB$suffix" ]; then
    mv -- "$LITE_DB$suffix" "$LITE_ROLLBACK/database$suffix"
  fi
done
if [ -d "$LITE_KEYS" ]; then mv -- "$LITE_KEYS" "$LITE_ROLLBACK/keys"; fi
cp -- "$LITE_SNAPSHOT" "$LITE_DB"
cp -a -- "$LITE_SNAPSHOT.keys" "$LITE_KEYS"
chmod 600 -- "$LITE_DB"
chmod 700 -- "$LITE_KEYS"
printf 'Preserved the original database and keys at %s\n' "$LITE_ROLLBACK"
# Restart the Lite service/container, then perform the validation above.
)
```

Replace the example snapshot placeholder before running the commands. If any command fails, leave
all preserved files in place and resolve the problem before restarting. On Windows, perform the same
sequence after stopping the process/service; preserve sidecars and both key directories, use restrictive
ACLs, and account for the service identity. This procedure does not require PostgreSQL tools.

## Full: pgBackRest backups and recovery

FrostStream core backups are **pgBackRest** backups of the PostgreSQL cluster (the
`froststreamdb`, `authentikdb`, and `openfgadb` databases), paired with an **OpenBao Raft
snapshot** (its actual storage — everything in the vault, not just one mount) plus a **KV v2
secrets export** as a human-readable fallback, per backup. Continuous WAL archiving makes
point-in-time recovery (PITR) possible to any moment after the oldest full backup. Media files,
local import source files, Typesense data, NATS runtime state, and worker caches are
intentionally excluded — they are rebuildable or live elsewhere.

## Architecture

Everything runs co-located on the shared container volumes; there is no pgBackRest TLS/SSH
repository-host mode and nothing needs PostgreSQL tools on the host:

- The **postgres** container is a custom image (`App/FullApp/PostgresServer/Dockerfile`: stock
  `postgres:18.3` + pgbackrest). Its `archive_command` is
  `pgbackrest --stanza=froststream archive-push %p`, pushing every completed WAL segment into
  the shared repository.
- The **backupservice** container (also postgres-based, with the ASP.NET runtime and pgbackrest)
  runs backups, verification, and restores as *local* pgBackRest operations. It shares with
  postgres:
  - the backup root bind mount (`FROSTSTREAM_BACKUP_ROOT` → `/backups`; repository at
    `/backups/pgbackrest`, OpenBao exports at `/backups/openbao`, job records at `/backups/jobs`),
  - the data volume `froststream-postgres-data` (`/var/lib/postgresql`; PGDATA is
    `/var/lib/postgresql/18/docker`),
  - the socket volume `froststream-postgres-socket` (`/var/run/postgresql`) for pgBackRest's
    local libpq connection.
- Both containers run as uid 999 (`postgres`), so files written by one are natively owned
  correctly for the other.
- Configuration is one file, `src/App/FullApp/AppHostFull/configs/pgbackrest/pgbackrest.conf`, mounted
  read-only into both containers. Compression (`compress-type=zst`) and retention
  (`repo1-retention-full=4`, `repo1-retention-diff=14`) live only there — pgBackRest expires old
  backups (and their WAL) automatically after every backup, and BackupService prunes the paired
  OpenBao exports to match.
- BackupService creates the stanza automatically at startup (`stanza-create` + `check`); until
  it has run once on a fresh repository, postgres retries `archive-push` per segment, which is
  harmless.

## Backup Types And Schedules

| Type | pgBackRest | Contents | Schedule (seeded) |
| --- | --- | --- | --- |
| `full` | `backup --type=full` | Complete cluster copy | `backup-full`, weekly Sun 03:00 UTC |
| `diff` | `backup --type=diff` | Changes since the last full | `backup-diff`, daily 02:00 UTC |

Every backup also gets `--annotation=name=<name>` (the name entered in the admin UI, or a
generated `scheduled-…` name) and a same-moment OpenBao backup at `/backups/openbao/<label>.*`,
each file paired with a `.sha256` sidecar.

Scheduled backups are dispatched by the Scheduler **over REST** directly to BackupService
(`BackupService__BaseUrl`); the Scheduler polls the job to completion, records the schedule
marks, and raises the `BackupFailed` admin notification on failure. The Jobs → Background run
row is reported by BackupService itself, so manual and scheduled backups look identical there.

## OpenBao Backup

Each pgBackRest backup pairs with two OpenBao artifacts, written over OpenBao's HTTP API (no
extra container access needed — this works the same in Aspire run mode and in compose/production):

- **`<label>.raft-snapshot`** — an online snapshot of OpenBao's actual storage (`GET
  /v1/sys/storage/raft/snapshot`), the same mechanism `bao operator raft snapshot save` uses.
  OpenBao's storage backend is Raft/BoltDB (`storage "raft"` in `openbao.hcl`, backed by the
  `openbao-data` volume); this is the *only* safe way to back it up consistently while it's live
  — a raw copy of that volume risks catching a BoltDB file mid-write. Restoring it (via
  `snapshot-force`, since a normal restore-forward safety check would otherwise reject
  intentionally rolling back to older data) replaces **everything** in the vault — secrets, auth
  backends, policies, the token store — with its state at backup time. This is the authoritative
  backup and the recommended restore path.
- **`<label>.json`** — the pre-existing logical KV v2 export (recursive read of the configured
  `secret/` mount over the API). Kept as a human-readable fallback in case the snapshot restore
  can't be used for some reason; restoring it only replays individual KV values, not the rest of
  the vault's state.

Both need OpenBao already unsealed and reachable at backup time (export) or restore time. The
restore console's finish step offers both, snapshot first.

## Admin Surface

**Admin → Backups** (or the API below) can start backups, watch jobs, browse the repository,
and run verification. Restores happen in the standalone restore console instead (next section).

- `POST /api/global/backups` — `{ name?, type: "full" | "diff" }` → 202 + job
- `GET /api/global/backups` — repository listing: labels, types, names, sizes, WAL ranges,
  OpenBao-export presence, repository health, and the PITR window
- `GET /api/global/backups/jobs`, `GET /api/global/backups/jobs/{jobId}` (includes a live
  output tail)
- `POST /api/global/backups/verify` — `{ label?, deep }` → 202 + job

### Two-tier verification

- **Quick verify** — `pgbackrest verify`: checks every backup file and archived WAL segment
  checksum in the repository. Cheap; run it any time.
- **Deep verify** — proves a backup actually restores: BackupService restores the chosen backup
  (or the latest) into `/backups/.deep-verify`, starts a throwaway PostgreSQL on it
  (socket-only, archiving off), confirms the three databases exist and each contains user
  tables, then tears everything down. Needs free disk roughly equal to the database size.

## Restore (Standalone Console)

Restores run from the **restore console** at `http://<host>:25900` (port
`PORT_BACKUP_RESTORE_UI`), a token-protected wizard served by the backupservice container on a
second port. It works while everything else — including Authentik sign-in — is down; the token
is `BACKUP_RESTORE_UI_TOKEN` from the deployment's `.env` / environment.

The wizard walks through:

1. **Prerequisites** — postgres container stopped (a stale `postmaster.pid` can be cleared from
   the wizard), repository healthy, at least one backup, data volume writable.
2. **Select** — latest (backup + all archived WAL), a specific backup label, or **point-in-time
   recovery** to any moment inside the recoverable window.
3. **Confirm** — type the stanza name (`froststream`).
4. **Restore** — `pgbackrest restore --delta` into the shared data volume, with live output.
5. **Finish** — start the postgres container (it replays WAL to the target and promotes), start
   the rest of the stack, optionally restore OpenBao's paired backup from the wizard (Raft
   snapshot first, KV-only export as a fallback — see "OpenBao Backup" above), and take a fresh
   full backup (the old timeline's later WAL is no longer meaningful).

Typical compose flow:

```bash
cd src/App/SharedApp/docker-compose-artifacts
docker compose stop webapi databridge worker scheduler mediaprocessor frontend authentik authentik-worker openfga postgres
# open http://<host>:25900 and run the wizard
docker compose start postgres    # watch logs until "ready to accept connections"
docker compose up -d
```

Afterwards trigger a metadata search reindex so Typesense is rebuilt from PostgreSQL.

**Break-glass fallback** (wizard unavailable): the same operations are plain pgbackrest
commands inside the backupservice container, e.g.
`docker compose run --rm --entrypoint pgbackrest backupservice --stanza=froststream info` or
`… restore --delta --type=time --target='2026-08-03 12:00:00+00' --target-action=promote`.

## AppHost / Aspire Configuration

- `FROSTSTREAM_BACKUP_ROOT` controls the host directory bind-mounted at `/backups` in both the
  postgres and backupservice containers (default `<storage-root>/core-backups` under Aspire,
  `./backups` beside the generated compose file). AppHost pre-creates and world-writes the
  repo/openbao subdirectories in run mode; the compose export gains a one-shot `backup-init`
  container that `chown`s the bind mount to uid 999 before postgres starts.
- `src/App/FullApp/AppHostFull/configs/postgres/postgresql.conf` (mounted with `-c config_file=…`) pins
  `wal_level=replica`, `max_wal_senders`, `archive_mode=on`, and the pgbackrest
  `archive_command`. Changing `archive_mode`/`archive_command` requires the container to be
  recreated.
- `src/App/FullApp/AppHostFull/configs/postgres/pg_hba.conf` adds a `local all postgres peer` rule so
  pgBackRest's socket connection needs no password (BackupService also exports `PGPASSWORD` as a
  fallback), plus the SCRAM network rules.
- BackupService env: `Backup__Stanza`, `Backup__PgDataPath`, `Backup__Postgres*`,
  `Backup__OpenBao*`, `Backup__RestoreUiToken`. The internal API port (24050 → 8080) is never
  published; only the restore console port (25900 → 8081) is.

## Upgrading From The Pre-pgBackRest Backup System

The old snapshot (`pg_dump`) / full (`pg_basebackup`) / hand-rolled WAL archive system is gone
and this is a **clean cutover** — old archives under `<backup-root>/archives` and the old
`<backup-root>/wal` directory are not readable by the new code. If you may ever need them,
restore with a pre-rework checkout; otherwise delete both directories after the first verified
full backup.

One-time steps on an existing deployment:

1. The postgres data volume is now explicitly named `froststream-postgres-data`. Rename the old
   auto-named volume (`podman volume rename apphost-…-postgres-data froststream-postgres-data`
   before first start) or accept a fresh database.
2. The scheduled-backup JetStream consumer is gone; on a live NATS store run
   `nats consumer rm FROSTSTREAM_BACKGROUND databridge-backup` once (dev: wiping the NATS file
   store also works) so the topology update that drops its subject can apply.
3. Migration 086 renames the `backup-snapshot` schedule row to `backup-diff`.
4. Set `BACKUP_RESTORE_UI_TOKEN` in the environment / compose `.env`.

## First Compose Start: OpenBao

OpenBao uses a persistent single-node Raft volume instead of ephemeral `-dev` mode. On a new Compose
deployment, initialize and unseal it before starting the application:

```bash
cd src/App/SharedApp/docker-compose-artifacts
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
`docker compose exec openbao bao operator unseal` before dependent services become healthy.

The manual procedure above applies when you initialize the vault yourself. Both `aspire run` and the
Compose export otherwise ship an `openbao-bootstrap` helper that performs a one-share
initialization and unseal automatically. It writes the generated unseal key and root token to the
host-mounted `openbao-bootstrap/init.env` file instead of the `openbao-data` volume. That makes the
recovery material independently backupable; it is also highly sensitive and must never be committed
or included in ordinary unencrypted backups.

For Compose, the default directory is beside `docker-compose.yaml`. Create it before the first
start and restrict it to the account that runs Compose. Linux/rootless Podman users should use mode
`0700`; on Windows/Docker Desktop, keep it in a user-owned NTFS directory with restrictive ACLs.
Override the location with `FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT` (a path usable by the container
runtime) when needed. Back up the resulting file to encrypted, off-host storage:

```sh
mkdir -p openbao-bootstrap
chmod 700 openbao-bootstrap
docker compose up -d openbao openbao-bootstrap
cp openbao-bootstrap/init.env /secure-backup-location/openbao-init.env
```

In PowerShell on Windows, create the directory before `docker compose up`; retain access only for
the account that runs Docker Desktop:

```powershell
New-Item -ItemType Directory -Force .\openbao-bootstrap
icacls .\openbao-bootstrap /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F"
Copy-Item .\openbao-bootstrap\init.env <encrypted-backup-location>
```

The helper automatically migrates a pre-existing `.bootstrap/init.env` out of `openbao-data` on its
first run and removes the volume-resident copy only after the host file has been written. If an
initialized vault has neither file, it stops with a recovery error rather than reinitializing the
vault. Restore `init.env` from its secure backup, then start `openbao-bootstrap` again.

A single unseal share is a development convenience, not a production posture; for production,
initialize with multiple shares as described above and configure an external auto-unseal provider.

The application token retains the current root-level behavior for compatibility. Replacing it with
least-privilege policies/AppRole and configuring an external auto-unseal provider are separate
production-hardening tasks.
