# Phase 10 — exclusive local execution and durable dispatch evidence

Date: 2026-09-12

State: Accepted on 2026-09-13 by the request to implement Phase 11. This remains a development milestone, not a deployable Lite edition.

## Delivered behavior

`LiteExecutionLease` opens a dedicated PostgreSQL connection and obtains one stable session advisory
lock before the dispatcher hosted service starts. Failure to obtain it fails host startup with an
actionable second-runtime error. A probe monitors the ownership session. Connection failure cancels
the ownership token, cancels active work, requests host shutdown, and is never reacquired in-process.
Claims, recovery, cancellation, retries, and completions all execute on the owned connection. A stale
executor therefore cannot use another pooled connection to commit after ownership disappears.

Migration 99 creates `jobs.local_execution_work`, a PostgreSQL ledger with a unique deduplication key,
JSON payload, attempts, availability, terminal timestamps, and constrained states. Explicit application
initialization is now compatibility version 2. Startup changes interrupted `running` work back to
`queued`; an interrupted cancellation becomes terminal `cancelled`. Atomic `FOR UPDATE SKIP LOCKED`
claims and status-guarded completion make duplicate wake-ups and repeated completion harmless.

`LocalExecutionDispatcher` treats its bounded channel as a lossy wake hint and polls PostgreSQL as the
authority. Total concurrency is configurable and defaults to four; the `download` concurrency group
defaults to one. Per-work cancellation is persisted before its active token is cancelled. Retry results
honor `maximum_attempts` and a requested retry delay. No remote worker registration, NATS queue group,
heartbeat, tag routing, or distributed lease monitor is registered by Lite.

`GET /api/jobs/local` returns the current persisted snapshot. The all-work and per-work SSE routes
write that snapshot first, then bounded live state hints. The shared Phase 7 hub drops oldest frames
for slow clients; reconnect reloads PostgreSQL and does not rely on missed in-memory events.

Stage 10 intentionally registers no real media handler. `ILocalExecutionHandler` is the controlled
extension boundary used by the tests and reserved for Stage 11 acquisition wiring. Unknown kinds fail
durably with `handler_unavailable` instead of being silently consumed.

## Changed files

- `src/App/DataBridge/Lite/LocalExecutionModels.cs`, `LiteExecutionLease.cs`,
  `NpgsqlLocalExecutionStore.cs`, `LocalExecutionDispatcher.cs`, and registration extensions
- `src/App/DataBridge/Migrations/FluentMigrator/099_CreateLocalExecutionLedger.cs`
- `src/App/DataBridge/Initialization/ApplicationInitializationCoordinator.cs`
- `src/App/FrostStream.Lite/Program.cs`, `appsettings.json`, and
  `Controllers/LocalExecutionController.cs`
- `Tests/UnitTests/Lite/LocalExecutionDispatcherTests.cs`,
  `LiteExecutionPostgresTests.cs`, Lite composition tests, and initialization-version assertions
- `FROSTSTREAM_LITE_PHASES.MD`

## Automated verification

Commands:

```bash
dotnet build Tests/UnitTests/UnitTests.csproj --no-restore -m:1
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LocalExecutionDispatcherTests/*'
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LiteHostTests/*'

FROSTSTREAM_TEST_POSTGRES='Host=127.0.0.1;Port=35432;Database=froststream_phase10;Username=postgres;Password=synthetic' \
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LiteExecutionPostgresTests/*'

dotnet build src/App/FrostStream.slnx --no-restore -m:1
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll
git diff --check
```

Results:

- controlled dispatcher tests: 5/5 passed;
- real PostgreSQL ownership and ledger tests: 2/2 passed;
- unchanged Lite host/composition tests: 7/7 passed;
- solution build: passed with 0 warnings and 0 errors;
- complete unit suite: 512/516 passed. The same four unrelated pre-existing failures remain:
  `CreatePolicy_Returns_Accepted_When_OpenFga_Synchronization_Is_Deferred`,
  `Every_Controller_Endpoint_Has_Detailed_OpenApi_Metadata`,
  `Authentication_Challenge_Is_Reported_As_Permanent_With_Specific_Code`, and
  `Map_Uses_Per_Comment_Unknown_Account_Handle_When_Comment_Author_Is_Missing`.

The controlled fixture verifies one logical completion after 100 duplicate wake-ups, total concurrency
at or below three in the configured test, default download concurrency of one, active cancellation,
ownership-loss cancellation without completion, and recovery by a reconstructed dispatcher.

The real-driver fixture used disposable PostgreSQL 17. It rejected the second lease, terminated the
first lease's backend with `pg_terminate_backend`, observed application shutdown and refusal of stale
owned operations, then acquired with a replacement. Its ledger check verified duplicate enqueue,
atomic claim, persisted retry/reclaim at attempt 2, one successful/idempotent completion,
process-loss recovery of the same work ID at attempt 2, and a persisted terminal snapshot.

## Initialization and live smoke

Disposable PostgreSQL 17 and Typesense 30.2 containers were initialized with synthetic credentials.
The normal `DataBridge initialize` command applied migration 99, created the Cleipnir tables, owner and
Typesense collections, and recorded application initialization version 2. A development Lite host then
acquired backend ownership and served:

```text
GET /api/jobs/local
{"streamKey":"all",...,"items":[]}

GET /api/jobs/local/stream
event: snapshot
data: {"streamKey":"all",...,"items":[]}
```

The SSE client was deliberately slow/disconnected after one second. The host then handled SIGINT and
released its ownership session without an exception. Both containers were stopped and automatically
removed. `/tmp/froststream-phase10-data` contains only disposable synthetic initialization material.

## Repeatable crash and lock-loss scenarios

Crash recovery:

1. Enqueue a controlled handler item and wait until its ledger state is `running`.
2. Kill the Lite process without a graceful stop.
3. Start Lite against the same database. Expected: startup recovery returns the same work ID to
   `queued`, the next claim increments its attempt, and only one terminal completion is accepted.

Lock loss:

1. Read `LiteExecutionLease.BackendProcessId` in the controlled fixture.
2. From an independent PostgreSQL connection execute `SELECT pg_terminate_backend(<pid>)`.
3. Expected: the ownership token and active executor token cancel, the application stops, and the old
   dispatcher cannot persist completion. A new process can acquire the lock and recover the row.

## Known limitations

- Real downloads, creator discovery, imports, media processing, search/chat jobs, schedules, and backup
  handlers are not enabled in Lite until their owning phases. This phase proves the dispatcher with a
  controlled handler and deliberately reports unknown work kinds as failed.
- Live events are advisory and may be dropped for slow clients. The reconnect snapshot is authoritative;
  clients must treat state events as hints and reconnect when their stream breaks.
- PostgreSQL advisory locks are scoped to one database session. Network partitions are handled when the
  driver detects session loss; an executor still must honor its cancellation token promptly.
- The schema change requires rerunning the matching init profile. A version-1 installation is rejected
  by normal startup until initialization records version 2.

## Rollback

Stop Lite first. Remove the Stage 10 registrations, controller, execution/lease/store files, tests, and
migration 99, then restore application initialization compatibility version 1. If migration 99 has been
applied, drop only `jobs.local_execution_work` and remove version 99 from FluentMigrator's `VersionInfo`
before running older code. Do not delete the `jobs` schema or any existing download/workflow tables.
Full service composition and its NATS/OpenFGA/OpenBao resources were not changed.
