# Core Backup And Restore

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

- The **postgres** container is a custom image (`App/PostgresServer/Dockerfile`: stock
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
- Configuration is one file, `src/App/AppHost/configs/pgbackrest/pgbackrest.conf`, mounted
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
cd src/App/docker-compose-artifacts
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
- `src/App/AppHost/configs/postgres/postgresql.conf` (mounted with `-c config_file=…`) pins
  `wal_level=replica`, `max_wal_senders`, `archive_mode=on`, and the pgbackrest
  `archive_command`. Changing `archive_mode`/`archive_command` requires the container to be
  recreated.
- `src/App/AppHost/configs/postgres/pg_hba.conf` adds a `local all postgres peer` rule so
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
cd src/App/docker-compose-artifacts
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
initialization and unseal automatically. It keeps the generated unseal key and root token in
`.bootstrap/init.env` **inside the `openbao-data` volume**, so the key can never be discarded
independently of the storage it unlocks — and so the same behavior works under rootless Podman and
Docker Desktop on both Linux and Windows without any host path. Read them back with:

```sh
docker compose exec openbao cat /openbao/data/.bootstrap/init.env
```

A single unseal share is a development convenience, not a production posture; for production,
initialize with multiple shares as described above and configure an external auto-unseal provider.

The application token retains the current root-level behavior for compatibility. Replacing it with
least-privilege policies/AppRole and configuring an external auto-unseal provider are separate
production-hardening tasks.
