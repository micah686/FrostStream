# Durable workflow persistence (Phase 2e)

Full retains `Cleipnir.Flows.PostgresSql` 4.2.5's `UsePostgresStore`, table prefix `flows`, and `Search Path=cleipnir,public`. SQLite uses `SqliteFunctionStore` and the same flows and `NodaTimeFlowSerializer`. The application initializer creates SQLite workflow tables before any hosted consumers start, including initialization-only Lite. Phase 2 persistence is complete. Normal SQLite startup requires the Phase 3 local infrastructure adapters. See the [startup and upgrade handoff](../Schema/README.md).

## Verified pinned contract

The effective package pins are in `src/Directory.Packages.props`. The installed `Cleipnir.ResilientFunctions` 4.2.5 NuGet repository metadata identifies upstream commit [`019b221566784e015c01403376bf42c4f126bccc`](https://github.com/stidsborg/Cleipnir.ResilientFunctions/tree/019b221566784e015c01403376bf42c4f126bccc). The implementation was checked against that commit's interfaces, PostgreSQL store, SQL generator and recovery tests; no assumption about newer releases is required.

| Contract | Persisted data and operations |
| --- | --- |
| `IFunctionStore` | Type/instance key, human ID, parameter/result bytes, exception, status, epoch, expiry, interrupted flag, timestamp, parent; creation with initial effects/messages, bulk scheduling, restart with effects/messages, lease renewal, expired/succeeded discovery, parameter/admin state changes, success/failure/postponement/suspension, interruption, status/instance discovery, deletion. |
| `ITypeStore` | Stable, database-assigned flow-type IDs; insert-or-get and enumerate. |
| `IMessageStore` | Ordered position, payload/type bytes, optional idempotency key; append one/batches (implicit or explicit positions), optional atomic interruption, replacement, truncation, reads from position, maximum positions. |
| `IEffectsStore` | Serialized effect ID/context, hash ID, work status, bytes and exception; upsert, insert/update/delete changes, reads and removal. Workflow state is stored as effects by the shared library. |
| `ITimeoutStore` | Workflow key, serialized timeout effect ID and expiry; conditional overwrite, expiry discovery and removal. |
| `ICorrelationStore` | Correlation-to-workflow links; idempotent registration, both lookup directions and removal. |
| `ISemaphoreStore` | Group/instance FIFO positions and serialized workflow owners; idempotent acquisition, release and queue discovery. |
| `Utilities` / `IUnderlyingRegister` | Separate register/arbitrator namespaces with durable set-if-empty, compare-and-swap, get, exists and conditional/unconditional deletion. |
| `IMigrator` | Independently versioned, repeatable workflow schema initialization. |

These interfaces do **not** accept cancellation tokens. Application retention discovery does; workflow SQLite operations use the shared finite busy timeout rather than inventing incompatible overloads. Cancellation at the workflow level uses existing control-panel failure/deletion, not a new store status. Import work can be recovered by watchdogs; unfinished download flows remain subject to the application's intentional startup invalidation in `DownloadFlowStartupService` (integration verified in 2f).

## SQLite invariants

- Every connection uses `SqliteConnectionFactory`: foreign keys, WAL, private cache, disabled pooling and finite busy handling. SQLite ADO.NET I/O is synchronous; returning the contract's `Task` does not imply asynchronous disk I/O.
- Writes take a short immediate transaction **before** reading positions/epochs/queue state. Cross-process ownership comes from SQLite, with no process-local lock or in-memory fallback. Reads use a consistent deferred transaction. No workflow/user callback, network call or filesystem effect runs inside these transactions.
- Creation and its initial state, restart and its state snapshot, batch appends plus interruption, effect changes, semaphore acquire/release, compare-and-swap and deletion are atomic. Constraint failures dispose the transaction and roll back. No blind retry can replay an external workflow effect.
- Administrative parameter/state changes and execution restart increment epochs. Ordinary terminal/suspend/postpone transitions and lease renewal preserve epochs, matching the pinned PostgreSQL behavior. Suspension/postponement checks the interrupted flag to prevent a lost message wakeup; interruption wakes suspended/postponed work immediately. Only executing/postponed work is eligible for expiry recovery.
- Bytes remain bytes; timestamps/expiry remain the library's `DateTime` ticks (not application microseconds). Workflow GUIDs are canonical dashed strings inside this independent schema. Application GUIDs remain the existing `N` encoding. Effect and parent IDs use the library serialization methods.
- Messages may be appended before workflow creation; auxiliary tables intentionally have no FK to the workflow row, as with the pinned store. Idempotency keys are retained; the library's message reader performs deduplication. They are not unique database keys.
- SQLite persists transition effects/messages atomically when supplied. The pinned PostgreSQL implementation ignores those optional transition arguments; the shared 4.2.5 runtime flushes effects/messages through their own stores. Full's implementation is unchanged.
- Instance deletion removes effects, messages, timeouts and correlations in the same transaction, plus SQLite semaphore ownership. Register/arbitrator values are independent utilities and are not instance-owned.

## Schema and retention

`cleipnir_schema` has a singleton version row; v1 owns `cleipnir_flows`, `cleipnir_types`, `cleipnir_effects`, `cleipnir_messages`, `cleipnir_timeouts`, `cleipnir_correlations`, `cleipnir_semaphores` and `cleipnir_register` and their indexes. Fresh/empty version 0 advances atomically to v1; v1 reinitializes without modifying data. Unknown versions are rejected. Version 0 denotes an empty workflow schema, not an older application schema containing workflows. No previous SQLite workflow schema shipped. Future evolution must add an explicit migration; application baseline history is untouched.

The pinned PostgreSQL workflow migrator's current version is **0**, with an empty migration dictionary; it has no older supported workflow upgrade sequence. Tests verify fresh initialization and repeated initialize/migrate/reopen with preserved state. The application's separate FluentMigrator history remains unchanged.

`IWorkflowRetentionQueries` encapsulates terminal orphan discovery and terminal import-attempt selection. Full keeps its original SQL; SQLite uses shared instance parsers and indexed owner lookups. Filtering owners precedes the orphan limit, and import parameters are batched. Both purgers still recheck panel status and delete through their typed Cleipnir control panels. Executing, postponed and suspended flows are never retention candidates.

## Verification

From the repository root:

```sh
dotnet build Tests/UnitTests/UnitTests.csproj --no-restore
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll --treenode-filter '/*/*/WorkflowPersistenceTests/*'
```

Set `FROSTSTREAM_TEST_POSTGRES` to a **disposable server** connection string to run the same cases on PostgreSQL as well as SQLite. The existing fixture creates/drops an isolated database per case and replays Full's unchanged migrations. With no server configured, only SQLite runs.

The crash test launches the test executable as a child, persists an effect and progress, kills the entire child process, reopens the store and uses a new registry/watchdog to finish the work. It verifies the original parameter, incremented epoch and that the completed effect is not repeated. `CrashProcessProbe` is an internal test helper and a no-op during ordinary suite runs. Each crash fixture cleans its process and marker files in `finally`.
