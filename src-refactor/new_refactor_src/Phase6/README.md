# Phase 6: fresh installation and recovery

All commands below run from `new_refactor_src`. These are fresh installations, not migrations from older refactor phases or the original product. Generate into a new directory and use a unique Compose project name. Existing output is never overwritten, and verification never deletes volumes.

## Generate and start

Prerequisites: .NET 10, Aspire CLI, Python 3 with `Phase0/requirements.txt`, a Docker-compatible engine and Docker Compose. Generated files build the checked-out Dockerfiles; retain this checkout and its configuration files. Private `.env` and `installation.env` contain newly generated credentials. Keep their directory private and retain both files. The generated Full profile is a local development installation with real Authentik/OpenFGA, not certification of external HTTPS identity configuration.

```sh
python3 Phase6/package.py Lite .phase0/my-lite
python3 Phase6/package.py Full .phase0/my-full
# Optional live chat; creates one additional ClickHouse service.
python3 Phase6/package.py Lite .phase0/my-chat-lite --live-chat
# A custom host frontend port still maps to container port 25000.
python3 Phase6/package.py Lite .phase0/another-lite --frontend-port 25107

docker compose -p my-lite -f .phase0/my-lite/docker-compose.yaml up -d --build
python3 Phase6/check-package.py Lite .phase0/my-lite --base-url http://127.0.0.1:25000
```

Full uses 19 services with live chat off and 20 with it on, including one-shot initialization jobs. Lite uses 3 or 4. Optional ClickHouse exposes its own health probe. Backends wait for container start and probe an actual query with bounded retries; an unhealthy optional store can reach a Failed schema state without blocking the main application indefinitely. The frontend's `/healthz` reports frontend liveness; `/readyz` proxies application `/health`. Production hosts expose `/health` and `/alive`. Full readiness requires a reply from DataBridge handlers; Lite readiness requires reconciliation, handler registration and scheduler startup. `/api/search/status` reports search separately. `/api/system/integrations` reports search and live-chat **schema startup** state; it is not a continuous ClickHouse availability monitor. A failed optional schema does not bring down the main application.

The installed Podman Compose 1.6.0 client failed to start Full dependents of completed initialization jobs. Full verification used the Docker Compose client against the same Podman API instead. Do not replace `service_completed_successfully` with `service_started` to bypass initialization. Lite has no such one-shot dependencies.

## Schemas and configuration

Full uses the embedded checksummed `Full/Infrastructure/Postgres/Baseline/001-baseline.sql`, captured from a disposable PostgreSQL 18.3 M001–M098 installation with search intent schema and triggers. Workflow runtime tables are excluded and initialized by the pinned Cleipnir library. Captured timestamps become installation-relative values; cron schedules are recomputed at installation. Baseline DDL, seeds and checksum commit together under an advisory transaction lock. Repeated initialization preserves edits; unknown checksums and populated databases without the new baseline marker fail. Old migration definitions remain as characterization/reference inputs for later cleanup; normal Full initialization does not replay them.

SQLite preserves its checksummed application v1 catalog and explicit v2–v4 steps, workflow v1, local transport initialization and search-intent initialization. Preserving these checksums keeps snapshot verification and existing schema tests meaningful. Fresh installation runs this single ordered initialization recipe before serving requests; it does not adopt an unknown/unversioned database. The historical M097 SQLite catalog export remains a characterization artifact, not the current Full baseline.

Schema-only initialization is available in both packaged backends:

```sh
docker compose -p my-lite -f .phase0/my-lite/docker-compose.yaml run --rm --no-deps lite --Persistence:InitializeOnly=true
# Full needs its PostgreSQL service and initialized databases first.
docker compose -p my-full -f .phase0/my-full/docker-compose.yaml run --rm --no-deps databridge --Persistence:InitializeOnly=true
```

Fresh Lite defaults are `/data/core.sqlite`, `/data/keys`, `/data/backups`, incoming media under `/data/incoming`, and search under `/search`. In packaged Lite, `/data` bind-mounts to the generated installation's local `data/` directory (for example `.phase0/my-lite/data`); archived media is stored below `data/archives/`. `.phase0` is hidden in many file browsers, so enable “show hidden files” or open that path directly. When running the AppHost in development mode outside Compose, the default is `new_refactor_src/.phase0/data`; set `FROSTSTREAM_STORAGE_ROOT` to choose another absolute local folder. The rebuildable search index remains in its separate project-scoped named volume. Explicit database/key paths remain configurable. Full persistent stores remain project-scoped named volumes, with separate backup/bootstrap bind roots per installation.

## Resource defaults

Published Lite has a 4 GiB / 4 CPU backend limit, 128 MiB / 1 CPU frontend limit and 512 MiB / 2 CPU POT limit. Optional ClickHouse has 2 GiB / 2 CPUs; Full PostgreSQL has 2 GiB / 2 CPUs. Backend managed GC is capped at 64% of its container memory (`DOTNET_GCHeapHardLimitPercent=40`, hexadecimal); native allocations, Lucene and FFmpeg share the remaining container budget. Limits include child processes and are ceilings, not measured reservations or guarantees. The Lucene writer remains bounded at 16 MiB by default. Runtime images restart unless stopped; initialization jobs remain one-shot. Modify generated Compose deliberately to suit measured workloads, preserving separate mounts and startup conditions.

Phase 5's Lucene-only measurements remain available. The physical 8 GB / combined-workload acceptance gate remains open; these limits do not substitute for that benchmark. Full's combined operating footprint is not certified for an 8 GB machine.

## Backup ownership

| Data | Owner and recovery policy |
| --- | --- |
| Lite SQLite | In-process snapshot includes application, workflow, inbox/queue, staged objects and encrypted secrets. Keep `<label>.sqlite`, its **complete** `<label>.sqlite.keys/`, and `<label>.sqlite.recovery.json` together. |
| Lite key ring | Data Protection keys under the configured key path are essential. A database alone cannot recover encrypted secrets. Snapshots copy the complete ring, including retired keys. |
| Full PostgreSQL | BackupService/pgBackRest covers the entire physical cluster, including application, Authentik and OpenFGA databases. Retain the repository and required archived WAL. |
| Full OpenBao | Each successful core backup has paired `openbao/<label>.raft-snapshot`, KV fallback `.json`, and mandatory `.sha256` sidecars. Raft storage is authoritative; KV fallback does not restore auth, policies or token state. Retain original unseal/recovery material separately (`openbao-bootstrap/init.env` in the generated local profile). |
| Full browser key ring | `froststream-data-protection-keys` is separate from PostgreSQL/OpenBao. Back it up to preserve sessions; otherwise users must sign in again. |
| Media and retained sidecars | Operator owns a separate backup of configured local/network/object storage. SQLite/pgBackRest snapshots do **not** include media bytes, caption sources, thumbnails or chat sidecars. Restore those before derived-index rebuilds. |
| ClickHouse | Operator owns independent chat backup if sidecars are not retained. With all `live_chat.json` and required emote sidecars retained, it can be rebuilt with the application's live-chat backfill. Core database backup does not include ClickHouse volume data. |
| Search | Separate, derived volume. Rebuild from restored records and source sidecars; do not treat a newer retained index as authoritative. |
| NATS and active work | PostgreSQL recovery is not a NATS stream backup. Startup reconciliation settles interrupted download runs; explicitly retry interrupted work after recovery. |

Store a second copy of recovery sets and bootstrap material outside the machine and mounted volumes. Full backup capture is PostgreSQL followed by OpenBao, not a distributed transaction; pause secret-changing operations during a coordinated recovery-point backup when exact cross-store alignment is required. PostgreSQL PITR cannot invent corresponding historical OpenBao secret changes between paired backups.

## Lite offline restore

Stop the backend and preserve the original installation. Mount the snapshot, complete key backup and manifest at their original sibling paths. The command verifies checksums, SQLite integrity/foreign keys and actual secret decryption without generating substitute keys. It copies into private staging, verifies the staged bytes again, invalidates derived search and publishes the database/key directory together. Existing destinations are rejected. An interrupted command can leave a private `.recovery-*` staging directory; never point the application at staging.

```sh
docker compose -p my-lite -f .phase0/my-lite/docker-compose.yaml stop lite
docker compose -p my-lite -f .phase0/my-lite/docker-compose.yaml run --rm --no-deps lite \
  --Recovery:Snapshot=/data/backups/LABEL.sqlite --Recovery:VerifyOnly=true
docker compose -p my-lite -f .phase0/my-lite/docker-compose.yaml run --rm --no-deps lite \
  --Recovery:Snapshot=/data/backups/LABEL.sqlite --Recovery:Destination=/data/recovered
```

Set `Persistence__Sqlite__Path=/data/recovered/core.sqlite` and `Secrets__Local__KeyRingPath=/data/recovered/core.sqlite.keys` in a Compose override for `lite`, then `up -d` with both files. Keep `/search` separate; recovery invalidation forces reconstruction even when that volume is newer. Confirm `/readyz`, `/api/search/status` and expected records. Keep original data and keys until recovery is verified. Cross-machine recovery requires copying the full recovery set and authoritative media, not just reusing the original volume.

## Full offline restore

Keep BackupService reachable on port 25900 with its private `BACKUP_RESTORE_UI_TOKEN`; the internal API is blocked on that port. The restore UI and its liveness probe work with NATS/PostgreSQL stopped. Use `up -d --no-deps backupservice` to start it independently. Preserve backup roots, bootstrap material, configured service credentials, media and browser keys before restoring.

1. Stop application processes, Authentik, OpenFGA and PostgreSQL. Keep BackupService and an initialized/unsealed OpenBao available. The coordinator refuses cluster replacement while PostgreSQL is running.
2. Authenticate to the restore wizard. Select a backup or explicit PITR target. A snapshot restore stops at backup consistency, rather than silently applying later WAL; PITR uses the requested timestamp. Verify the selected backup has its paired OpenBao artifacts and checksums.
3. Restore the matching OpenBao Raft snapshot. Keep the original unseal material available; force restore replaces vault storage and token state. If restoring onto newly initialized vault storage, use the original snapshot's unseal keys afterward. Snapshot application is asynchronous; wait for it to finish before unsealing, and retry through temporary startup/seal responses. Use KV-only fallback only with its narrower scope understood.
4. Start PostgreSQL and verify recovered records. Restore media/sidecars from their separate backup policy.
5. Set `Search__RebuildOnStartup=true` on `databridge` in a temporary Compose override (or `SEARCH_REBUILD_ON_STARTUP=true` when generating a recovery profile). Start the stack and wait for search Ready. Remove the override after successful reconstruction so ordinary restarts retain the valid generation.
6. Confirm application readiness, expected database and secret values, Authentik/OpenFGA availability and optional chat schema state. Re-authenticate browsers if their separate key ring was lost.

## Repeatable verification

The scripts require owned `froststream-phase6-*` project names. They change only the named disposable installation, retain all volumes/backups, and write concise result JSON. Full verification exercises the packaged pgBackRest engine and the same checksummed Raft restore endpoint used by the pairing adapter; it does not automate browser interaction with the wizard or an external Authentik login.

```sh
python3 Phase6/verify-baselines.py
dotnet build FrostStream.slnx -m:1
dotnet run --project Tests/UnitTests/UnitTests.csproj
# Real PostgreSQL tests create/drop their own isolated databases.
FROSTSTREAM_TEST_POSTGRES='Host=...;Database=postgres;Username=...;Password=...' \
  dotnet run --project Tests/UnitTests/UnitTests.csproj
python3 Phase6/verify-lite-recovery.py .phase0/disposable-lite --project froststream-phase6-lite --output /tmp/lite-recovery.json
python3 Phase6/verify-full-recovery.py .phase0/disposable-full --project froststream-phase6-full --output /tmp/full-recovery.json
python3 Phase6/check-package.py Lite .phase0/disposable-chat-lite --live-chat --base-url http://127.0.0.1:25107
python3 Phase6/verify-optional-degradation.py .phase0/disposable-chat-lite --project froststream-phase6-chat --base-url http://127.0.0.1:25107 --output /tmp/chat-degradation.json
# After the Full recovery fixture has created its paired snapshot:
python3 Phase6/verify-fresh-vault.py .phase0/disposable-full --output /tmp/fresh-vault.json
# Use --podman-unshare with rootless Podman to read private uid-mapped backup files.
```

Verified: solution build; 621 unit tests with real PostgreSQL enabled; 3 Lite workflow tests; 55 final deployment tests; packaged Lite snapshot/key-ring recovery; packaged Full physical PostgreSQL and paired OpenBao recovery; empty-volume vault restoration with original unseal material and application token; both editions with live chat on/off; normal Full restart; optional chat failure and recovery; real POT ping and ClickHouse schema v1; baseline/source/boundary checks. The sole source/API inventory change is the new integration-status route and was explicitly reviewed and captured.

See the checked-in result JSON for the exact verification scope. External HTTPS identity/login, browser restore-wizard interaction, independent ClickHouse/media backup restoration and the physical 8 GB workload benchmark are separate acceptance areas.
