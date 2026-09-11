# Phase 4: explicit initialization and runtime compatibility

Date implemented: 2026-09-11

Status: Implemented, awaiting verification.

Phase 4 removes schema creation and fixed-owner seeding from ordinary DataBridge startup and stanza creation from ordinary BackupService startup. It adds finite explicit command modes usable by Full now and by the future merged Lite image. This remains a development milestone, not a deployable Lite edition.

## Implemented lifecycle

`DataBridge initialize` executes dependency-ordered, deadline-bound, retry-safe steps:

1. FluentMigrator PostgreSQL migrations through version 98.
2. Cleipnir's PostgreSQL workflow store initialization in the existing `cleipnir` schema.
3. Optional ClickHouse schema scripts when live chat is enabled.
4. Fixed-owner reconciliation only when single-user mode is enabled.
5. Required persistent-directory creation.
6. Typesense collection creation and schema reconciliation.
7. The `froststream.initialization_state` application-version marker, written only after every selected step succeeds.

Starting a new attempt first removes an older marker. A crash or failure therefore cannot leave a stale overall-success record. Retrying reruns idempotent migrations/DDL/upserts and writes version `1` only at the end.

Ordinary DataBridge startup does not invoke FluentMigrator, create Cleipnir tables, seed the owner, create ClickHouse tables, create Typesense collections, or create directories. Before hosted runtime work starts, it checks the durable initialization version, `cleipnir.flows`, all three Typesense collections and the current media field, optional ClickHouse schema version, and the required persistent directory. Empty or incompatible state reports the exact init profile. Typesense rebuild/incremental indexing and workflow recovery remain runtime responsibilities.

`BackupService initialize` creates/reconciles the configured pgBackRest stanza and requires `pgbackrest check` to succeed within five minutes. Ordinary BackupService startup only reads stanza info and fails with an init-profile instruction when the stanza is absent or incompatible. Authentik lifecycle code is unchanged.

The transitional Full graph contains `databridge-initialize` and `backupservice-initialize`, both with `restart: "no"`; the corresponding runtime service has a native `service_completed_successfully` dependency. Both legacy Full profiles still contain these resources until Phase 5 performs profile-specific membership.

## Changed areas

- `src/App/DataBridge/Initialization/`, migration 98, `Program.cs`, ClickHouse/Typesense startup, and the fixed-owner initializer.
- `src/App/BackupService/Program.cs`, `BackupServiceOptions.cs`, and `PgBackRestRunner.cs`; the recurring `StanzaStartupService` was removed.
- `src/App/AppHost/StartServices.cs`, `StartBackupService.cs`, and centralized deployment resource names.
- Focused DataBridge/backup unit tests and the phase plan/evidence index.

## Automated verification

Commands run from the repository root:

```bash
dotnet build src/App/FrostStream.slnx

dotnet test --project Tests/UnitTests/UnitTests.csproj --no-build -- \
  --no-ansi --disable-logo \
  --treenode-filter '/*/*/ApplicationInitializationCoordinatorTests/*'

dotnet test --project Tests/UnitTests/UnitTests.csproj --no-build -- \
  --no-ansi --disable-logo \
  --treenode-filter '/*/*/SingleUserOwnerInitializerTests/*'

dotnet test --project Tests/UnitTests/UnitTests.csproj --no-build -- \
  --no-ansi --disable-logo \
  --treenode-filter '/*/*/PgBackRestCompatibilityTests/*'

./eng/publish-production-profile.sh \
  frostream-full-init \
  /tmp/froststream-phase4-publish-2
```

Results:

- Solution build: passed, 0 warnings and 0 errors.
- Focused initialization coordinator: 4/4 passed, including dependency ordering, stale-marker clearing, retry, and timeout reporting.
- Fixed owner initialization: 2/2 passed.
- pgBackRest read-only compatibility behavior: 2/2 passed.
- Aspire native publication pipeline and its Compose validation: 8/8 steps passed.
- Generated Compose contains explicit `command: [initialize]`, `restart: "no"`, and `service_completed_successfully` for both new initializer/runtime pairs.
- Full unit suite: 463/468 passed. The five failures are unrelated existing failures in access-policy result mapping, endpoint metadata, user-note delete mapping, yt-dlp failure text, and unknown comment-author mapping.

## Disposable application-state verification

Synthetic PostgreSQL 18.3, Typesense 30.2, and ClickHouse 25.8 containers were launched without persistent host data. The DataBridge command used synthetic passwords and `/tmp/froststream-phase4-data`.

Observed checks:

- Empty initialization applied all 98 PostgreSQL migrations, created nine Cleipnir tables, applied ClickHouse schema version 1, created all Typesense collections, seeded one fixed owner, and recorded application initialization version 1.
- A controlled persistent-directory failure after PostgreSQL/Cleipnir/ClickHouse/owner steps exited nonzero naming `persistent-directories`. A direct query returned zero application success markers.
- Retrying with the valid directory succeeded. Direct queries returned `version=1`, fixed-owner count `1`, and Cleipnir table count `9`.
- Repeating successful initialization retained exactly one fixed-owner row.
- Ordinary startup against a separate empty database exited nonzero with: `database initialization version is missing; required version is 1` and instructed the operator to run `frostream-full-init`.
- Ordinary startup against initialized state passed the compatibility check and reached normal hosted-service activation. It then stopped on intentionally absent Full OpenBao configuration; the owner timestamp and FluentMigrator version remained unchanged, proving runtime validation did not seed or migrate.

## Manual reproduction and expected results

With disposable PostgreSQL, Typesense, and optional ClickHouse endpoints configured using synthetic credentials:

```bash
dotnet run --project src/App/DataBridge/DataBridge.csproj -- initialize
```

Expected: each selected initialization step completes, the process exits zero, `froststream.initialization_state` contains application version 1, and rerunning exits zero without duplicate owners.

Then start normally:

```bash
dotnet run --project src/App/DataBridge/DataBridge.csproj
```

Expected: compatibility checks pass without migration/seeding logs, then normal reconnect, recovery, consumer, and indexing services start. Against empty state, startup exits before hosted services and names the matching init profile.

For pgBackRest, first publish an isolated installation name and temporary host roots so its explicit volume names cannot select an existing Full installation:

```bash
FROSTSTREAM_INSTALLATION_NAME=froststream-phase4-check \
FROSTSTREAM_STORAGE_ROOT=/tmp/froststream-phase4-check/data \
FROSTSTREAM_BACKUP_ROOT=/tmp/froststream-phase4-check/backups \
./eng/publish-production-profile.sh \
  frostream-full-init \
  /tmp/froststream-phase4-check/compose

docker compose \
  -f /tmp/froststream-phase4-check/compose/docker-compose.yaml \
  up --build backupservice-initialize

docker compose \
  -f /tmp/froststream-phase4-check/compose/docker-compose.yaml \
  run --rm backupservice
```

Expected: the initializer exits zero after `stanza-create`/`check`; the runtime passes its read-only stanza check and begins serving. Repeating the initializer must not recreate credentials or fail.

After recording the result, stop and remove only this explicitly isolated fixture with `docker compose -f /tmp/froststream-phase4-check/compose/docker-compose.yaml down -v`.

## Known limitations and remaining gate

The application portion of the gate is verified against disposable services. A real pgBackRest stanza creation/check was not run because the available repository runtime uses non-disposable Full paths and the isolated PostgreSQL fixture was not archive/pgBackRest configured. The focused compatibility tests pass, but Phase 4 remains “awaiting verification” until the manual pgBackRest scenario above succeeds on disposable storage.

Podman Compose 1.6.0 still has Phase 1's pre-existing successful-one-shot dependency limitation. Phase 5 owns lifecycle portability and the true Full init/runtime profile split.

## Rollback

Revert migration 98 only after removing its single compatibility marker, restore DataBridge's startup `MigrateUp` call and hosted schema/seeder registrations, restore BackupService's stanza hosted service, and remove the two transitional AppHost initializer resources/dependencies. Do not delete application, Typesense, ClickHouse, PostgreSQL, pgBackRest, or shared media volumes. The new marker table contains compatibility metadata only; rolling source back does not require changing credentials.
