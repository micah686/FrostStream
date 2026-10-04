# Phase 2 implementation checklist and handoff

Authoritative scope: [phase2plan.md](phase2plan.md). Design: [persistence architecture](phase2-persistence-architecture.md). Evidence: [source inventory](phase2-persistence-inventory.md).

## 2a — completed

- [x] Map shared entity declarations, EF configurations, repository/read contracts and raw SQL across modules.
- [x] Identify enum/schema, JSON, array, timestamp, dialect, upsert, bulk, lock and transaction dependencies, including unbounded timeouts.
- [x] Define provider composition/model, operation, atomic mutation and workflow retention boundaries; record nonportable operations.
- [x] Verify PostgreSQL source migrations 1–97 and separate Cleipnir schema ownership; select an independent SQLite baseline at the M097 application schema cutoff.
- [x] Record ordered implementation and validation tasks without changing runtime behavior.

## 2b — connection and schema foundation — completed

- [x] Add provider registration/migration initialization boundaries; Full must retain its current PostgreSQL connection, migration scan and history.
- [x] Add explicit Lite persistence opt-in and configurable SQLite path, default `/data/frostreamlitedb` (plan spelling is intentional).
- [x] Centralize connection setup: foreign keys per connection, WAL, finite busy budget, cancellation; include requirements for later workflow connections.
- [x] Build a disposable PostgreSQL database through M097 and export/review the final schema manifest (tables/columns/defaults/FKs/unique/check constraints/indexes/seeds). Reconcile EF and every raw SQL owner from the inventory.
- [x] Implement independent SQLite baseline v1 and application version table with flattened names and agreed type encodings; include raw-only metadata, policy, watch/like, circuit and statistics tables; removed maintenance tables are excluded. Preserve only active seeds.
- [x] Split shared EF model configuration from provider types/defaults and verify model cache behavior. Record enum label/GUID/time conversion rules used by both EF and raw commands.
- [x] Validate initialization twice, schema manifest equivalence, expected indexes, unique/check constraints and FK rejection on every connection. Confirm Full migrations still run unmodified.
- [x] Keep Lite opt-in/experimental; successful schema creation does not imply repository/workflow readiness.

## 2c — repositories and mappings — completed

- [x] Port ordinary EF repositories first: presets/config sets, schedules, creator discovery, imports, playlists and users/storage. Exercise direct-EF consumers too.
- [x] Extract operation contracts for embedded SQL; retain shared handlers, DTOs and ownership/validation rules.
- [x] Port metadata graph write/read, raw note search, rendition/encoding queries, account/caption/thumbnail reads and search hydration.
- [x] Port watch/likes, policy persistence, deletion/cleanup, provider circuits, deduplication and statistics SQL through selected adapters. Keep optional ClickHouse persistence separate; port its application DB marker/backfill queries.
- [x] Test native enum labels versus existing string enums; JSON/null/empty collections; GUID identity; UTC microsecond precision/pre-epoch/date semantics; generated IDs and deterministic aggregate/latest ordering.
- [x] Verify ordinary transactions and all upsert conflict targets, COALESCE behavior, affected rows and atomic increments on both providers. Keep SQLite FK failures/unique conflicts observable.
- [x] Reuse behavioral cases with real PostgreSQL and SQLite fixtures. Existing EF InMemory repository tests do not prove SQL, constraints or transaction portability.

## 2d — bulk and contention — completed

- [x] Port import batches (500), discovery upserts, set updates, metadata child replacement, playlist reorder and retention/delete batches; bound SQLite parameter counts.
- [x] Implement atomic download mutation boundary preserving run/attempt/lease guards. SQLite must acquire writer ownership before read-modify-write; Full retains row locks.
- [x] Use short database-only transactions, finite retry/busy budgets and cancellation. Preserve connection/transaction sharing between EF and raw operations.
- [x] Exercise concurrent claim/start/stop/results, duplicate messages, conflicting reorder, concurrent upserts and purge/write races; prove rollback and no partial graph updates.
- [x] Verify retries cannot repeat external side effects and do not hide non-contention errors; record retry/timeout outcomes. Review zero-command-timeout sites from inventory.

## 2e — durable workflow store (after stable persistence primitives)

- [x] Inspect exact pinned Cleipnir/ResilientFunctions 4.2.5 store interfaces and schema initialization/versioning. Record the complete supported contract before writing the store.
- [x] Implement all required SQLite workflow state, effects/messages, leases/epochs, timeout/scheduling and recovery primitives; no in-memory fallback.
- [x] Retain PostgreSQL UsePostgresStore and shared flows/NodaTimeFlowSerializer; initialize SQLite workflow schema independently of application baseline history.
- [x] Replace terminal/orphan discovery SQL in both purgers with the workflow retention query boundary; keep actual instance deletion through Cleipnir control panels.
- [x] Verify creation, message/effect round trips, progress, completion, cancellation, crash recovery and workflow schema upgrades against both stores.
- [x] Distinguish recoverable import workflows from intentionally interrupted download runs on startup.

## 2f — upgrades and restart closure (after 2b–2e)

- [x] Add portable evolution descriptions where possible and separate provider migration implementations where necessary; test data-preserving SQLite rebuilds and independent histories.
- [x] Verify fresh/upgrade paths for each supported SQLite version and Full's unchanged PostgreSQL sequence.
- [x] Port/verify the existing blocking DownloadFlowStartupService/ReconcileForStartupAsync behavior: queued stopped, active interrupted/failed, groups settled, leases expired, old flows invalidated before ingress/results.
- [x] Verify immutable RunId/AttemptId and generation gates reject stale queued/result messages after restart; cover cancellation and restart during mutation.
- [x] Run all applicable shared repository/workflow behaviors on both providers including contention, restart and retention.
- [x] Record deferred issues and Phase 3 persistence handoff. Only then mark Phase 2 complete.

## 2a handoff

Changed files: this checklist, `phase2-persistence-architecture.md`, generated `phase2-persistence-inventory.md`, `tools/persistence_inventory.py`, and completion links in `phase2plan.md`.

Decisions: retain shared models/contracts/business logic; isolate provider mappings and operation SQL; combine locking with atomic mutation; abstract workflow retention queries; baseline SQLite independently at Full's M097 application schema. Runtime registration, packages and PostgreSQL migrations are unchanged.

Verification commands: `python3 SPLIT_PLAN/tools/persistence_inventory.py --check`, `git diff --check`, and `dotnet build src/App/Lite/Lite.csproj --no-restore`. Result: build passed with 0 warnings and 0 errors; inventory drift and whitespace checks passed. Documentation link targets and the 45 DbSet/23 EF enum counts were checked against source.

Remaining evidence: live PostgreSQL catalog manifest in 2b; SQLite query/type/constraint equivalence in 2c–2d; exact pinned Cleipnir durable-store contract in 2e; cross-provider upgrades/restart correctness in 2f. These are later acceptance checks, not claims made by 2a. No current blocker to beginning 2b; its first concrete task is provider registration plus manifest capture. PostgreSQL-to-SQLite data conversion remains deferred; Phases 3–4 infrastructure stays out of this increment.

## 2b handoff

Changed files: `DataBridge/Persistence` (provider registration/initializers, shared model hints, PostgreSQL and SQLite provider passes, connection setup, embedded schema/catalog); shared EF configurations and DbContext; DataBridge module startup; Lite initialization-only entry point; central SQLite package reference; foundation tests; schema export/baseline tools; plan/checklist/architecture/inventory notes. PostgreSQL migration source files are unchanged.

Implemented: explicit `Deployment:Mode=Lite` + `Persistence:Sqlite:Enabled=true`; configurable `/data/frostreamlitedb`; shared FK/WAL/private-cache/bounded-busy connection setup for raw and EF opens; SQLite application baseline/version 1 independent of Full's history; atomic baseline/seeds/history and checksum validation; provider model mappings using shared configuration hints. EF's provider-specific service providers isolate model caches; both models coexist in the focused tests without an extra cache key factory.

Catalog evidence: unchanged migrations ran through 97 on a fresh PostgreSQL 18.3 database. Export captured 82 application tables, 66 FKs, 16 application checks and 96 non-primary indexes, plus the default storage and ten schedule seeds. A second fresh PostgreSQL database initialized through the refactored startup path produced the identical normalized catalog. Repeated PostgreSQL initialization also succeeded. The 45 EF model table/column mappings match the catalog. Static qualified table references in runtime SQL were reviewed against it; the only actual table outside this application catalog is `cleipnir.flows`, intentionally owned by 2e. Dynamically selected metadata taxonomy/policy child tables are included in the captured catalog.

Scope decisions: the SQLite opt-in supports `Persistence:InitializeOnly=true` and database service composition for verification. Normal SQLite runtime startup fails clearly before messaging/workflow consumers register because those adapters are 2c–2e. No mixed SQLite application/PostgreSQL workflow fallback is installed. Active Full seed definitions are preserved, with current shared-clock timestamps and Quartz next-due calculation; later phases adapt backup schedules. SQLite database-generated clocks have millisecond resolution while stored application times retain microseconds. See the [schema README](../src/App/DataBridge/Persistence/Sqlite/Schema/README.md) for commands and constraints.

Verification: 11 real-file SQLite foundation tests passed (schema/catalog comparison, checks/FKs/cascades, raw/async/reopened-EF pragmas, repeatable seeds, native enum/GUID/negative timestamp round trips, provider model isolation, history rejection, migration rollback/retry, concurrent initialization, bounded lock contention, pre-cancellation). Lite and UnitTests builds pass with zero warnings/errors. Baseline/inventory drift checks pass. Full unit suite result: 479 passed, 5 failed out of 484; the same five failures were reproduced on the unchanged HEAD (`102d16a`, 2a) in an isolated worktree (468 passed, 5 failed of 473). Existing failures: `AccessControlControllerTests.CreatePolicy_Returns_Accepted_When_OpenFga_Synchronization_Is_Deferred`, `EndpointMetadataTests.Every_Controller_Endpoint_Has_Detailed_OpenApi_Metadata`, `UserNotesControllerTests.Delete_Maps_NotFound`, `YtDlpFailureDetailsTests.Authentication_Challenge_Is_Reported_As_Permanent_With_Specific_Code`, and `YtDlpMetadataMapperTests.Map_Uses_Per_Comment_Unknown_Account_Handle_When_Comment_Author_Is_Missing`. They are outside 2b.

Next: 2c starts by extracting raw SQL operations into the provider boundaries and running shared repository cases on PostgreSQL and SQLite. Native enum/time/GUID/JSON storage primitives are in place, but reads/writes/dialect/upsert behavior is not yet ported. 2d owns contention beyond schema initialization; 2e owns the Cleipnir store; 2f closes upgrades and download restart verification. SQLite application versions after v1 are not implemented by this foundation.

## 2c handoff

Implemented: shared connection/command, parameter, reader and unique-conflict adapters; 141 explicit PostgreSQL/SQLite SQL statement and fragment pairs grouped by operation; ordinary EF and raw repositories and their direct consumers. Metadata graph writes/reads, notes/search hydration, policies, watch/likes, downloads/circuits/deduplication, rendition/media asset queries, statistics, cleanup and optional ClickHouse application markers now use the selected application provider. PostgreSQL query text and migration source remain unchanged. See the [query adapter README](../src/App/DataBridge/Persistence/Queries/README.md) for the contracts and test commands.

Mapping decisions: SQLite array parameters use JSON plus `json_each`, superseding 2a's proposed expanded parameter lists. Raw GUIDs, native enum labels, JSON/null values and UTC microseconds match the EF encodings. GUID search preserves dashed text; epoch rounding and UTC date buckets preserve PostgreSQL behavior, including negative instants and month-end clamping. Caption ordering preserves native enum order. Media root deletion explicitly shares a transaction with policy cleanup on SQLite. Unique conflicts are classified across providers without hiding other constraint failures.

Verification: all 16 focused tests pass, comprising 15 shared behavioral cases run against both real PostgreSQL and real-file SQLite, plus SQLite preparation of over 120 complete catalog statements. Existing PostgreSQL watch-state and media-deletion integration fixtures pass (4 and 8 tests). Lite, UnitTests and IntegrationTests build successfully. Full unit suite: 495 passed, 5 failed out of 500; these are the same five pre-existing failures recorded in the 2b handoff. SQLite baseline and inventory drift checks and whitespace checks pass.

Next: 2d owns bulk operations and writer ownership before read-modify-write, contention/retry boundaries and races. Workflow retention queries and the durable SQLite Cleipnir store remain 2e; upgrades/restart closure remain 2f. Normal SQLite runtime stays gated until those increments are complete. Optional ClickHouse storage retains its own SQL and connection.

## 2d handoff

Changed files: `ApplicationMutation`, `ApplicationBatches`, `ApplicationRetention` and the import-session lock query; download/job/import/discovery/metadata/playlist repository wrappers; both retention purgers; media deletion and stale-media maintenance; finite SQLite/raw command timeouts; shared fixture and contention tests; PostgreSQL media-deletion fixture composition; plan/architecture/inventory and [mutation contracts](../src/App/DataBridge/Persistence/Mutations.md). PostgreSQL migrations, the captured M097 catalog and SQLite v1 baseline are unchanged.

Implemented: SQLite acquires immediate writer ownership before mutation reads; PostgreSQL retains row locks and uses serializable transactions. Complete database-only operations replay classified contention with fresh tracked state and bounded jitter/budgets. Only named natural-key races in identity-reusing PostgreSQL operations retry unique violations; plain creation, other constraints and unknown failures remain errors. Nested calls share the same EF/raw transaction. Pending caller changes and caller-owned mutation transactions are rejected; the single-statement processed-message insert still supports a caller-owned transaction. Notifications and best-effort statistics run after commit and outside retries. Retry, timeout, cancellation and duration metrics identify operation/provider.

Bulk decisions: preserve 500-item import flushes while atomically committing the full submitted scan and counters; lock the PostgreSQL session before reading paths. Bound GUID membership and retention write batches to 400 parameters/roots. Coordinate discovery/source upserts, metadata replacement, content reservation, set updates and playlist reorder before their reads; deduplicate submitted playlist staging entries. Expired leases are processed at most 500 at a time. Attempt redelivery does not reset claimed work, and obsolete artifacts/leases cannot mutate a fresh run.

Deletion decisions: shared application retention rechecks age, terminal/group/sibling state, active leases and drained groups inside each delete transaction; cleanup touches dependent history only for deleted jobs. Stale-media cleanup uses short 400-root transactions. Worker file deletion runs once outside database retries; the database checks active downloads and file snapshots and returns a conflict when they changed. Flow panels remain outside application transactions and live panels are skipped. PostgreSQL workflow-instance discovery remains 2e; its query timeout is now finite, as are vacuum/reindex commands.

Verification: 34 focused persistence tests passed, including all 16 existing 2c cases and 18 new 2d cases. Seventeen new cases ran against both real PostgreSQL and real-file SQLite; the eighteenth verifies SQLite held-writer timeout metrics. Coverage includes concurrent redelivery/start/claim/result/finalize/stop, heartbeat/expiry and stale leases, conflicting reorder, content/source/discovery upserts, 1,201-item import rollback/deduplication, a reduced SQLite variable limit, metadata rollback, cancellation, post-commit callback behavior, purge/restart races and media objects added during Worker deletion. Temporary PostgreSQL fixtures disable pooling and bound simultaneous cases to avoid exhausting a stock server's client limit. Full unit suite with PostgreSQL enabled: 513 passed, the same five pre-existing failures from 2b, out of 518. PostgreSQL download repository, media deletion and watch-state integration fixtures passed (24 + 8 + 4). Lite, UnitTests and IntegrationTests builds pass; baseline/inventory/whitespace checks pass. All original 141 PostgreSQL catalog statement/fragment values remain unchanged; the separate mutation family adds one lock statement.

Next: 2e must inspect the exact pinned Cleipnir/ResilientFunctions 4.2.5 contract, implement its durable SQLite store and move the remaining terminal/orphan workflow-discovery SQL behind that store boundary. Then 2f verifies upgrades and restart closure. Normal SQLite runtime remains gated through 2f; there is no in-memory or mixed-provider workflow fallback.


## 2e handoff

Completed 2026-10-03. Verified the exact installed ResilientFunctions 4.2.5 source commit `019b221566784e015c01403376bf42c4f126bccc` and all required store interfaces before implementing SQLite. See [workflow persistence contracts and test commands](../src/App/DataBridge/Persistence/Workflows/README.md).

Changed files: new `Persistence/Workflows` database/session, complete function and auxiliary stores, provider composition, retention query boundary and contract notes; persistence registration and DataBridge initialization; both workflow-aware purgers; shared workflow tests and two updated foundation schema assertions; plan, architecture and regenerated source inventory. Packages, PostgreSQL migrations and SQLite application baseline remain unchanged.

Implemented: independently versioned SQLite workflow v1 schema in the selected application database, preserving function state/parent/epoch/lease/interruption, type identities, effects (including library workflow state), ordered messages, timeouts, correlations, FIFO semaphore queues and register/arbitrator compare-and-swap. Immediate transactions coordinate writers across processes. Creation/initial state, restart snapshots, message/effect batches, utility updates and deletion roll back atomically; busy waits remain finite through the shared factory. Workflow operations follow the pinned Task-only interface; application retention retains cancellation tokens.

Composition/retention decisions: Full still uses the pinned `UsePostgresStore`, `flows` prefix, `cleipnir,public` search path and shared flows/NodaTime serializer. SQLite initialization-only startup now initializes both independent schemas before consumers. The provider-specific flow configuration is ready for 2f; normal SQLite runtime remains gated. Terminal discovery moved out of both purgers; Full's discovery SQL is preserved, SQLite uses shared strict instance parsers and owner lookups before applying the orphan limit. Import selections batch parameters. Deletion stays with typed control panels and rechecks terminal status.

Recovery/upgrade decisions: watchdog recovery preserves import-style durable progress; download runs retain their intentional blocking startup invalidation/reconciliation, to be integrated and verified in 2f. SQLite supports a fresh/empty workflow version 0 to v1 transition, atomic/repeatable initialization and rejection of unknown versions. No older SQLite workflow schema shipped. The pinned PostgreSQL workflow migrator is at version 0 with no upgrade scripts; there is no earlier supported workflow schema migration to invent. Repeated initialize/migrate/reopen preserves state on both stores.

Verification: 12 shared behavioral tests pass against real-file SQLite and PostgreSQL 18.3, plus two SQLite-only schema/busy tests (15 reported tests including the no-op subprocess helper). Coverage includes creation, initial effects/messages, progress and serialized NodaTime payloads, completion/failure, epoch guards/lease renewal, suspend/interrupt/postpone wakeups, scheduling, durable auxiliary state, FIFO semaphores, concurrent creation/message append/restart/type registration, transactional rollback, cancellation through a control panel, retention owner/shape/status/limit checks and schema reinitialization. The restart test kills a subprocess after a persisted effect, then verifies a new registry/watchdog resumes with the original parameter, advances the epoch and does not repeat completed work. The full unit suite with PostgreSQL enabled reports **528 passed, five failed out of 533**: the same five pre-existing failures documented in 2b–2d. UnitTests, Lite Release and IntegrationTests builds pass with zero warnings/errors. Application baseline, source inventory and whitespace checks pass. Temporary PostgreSQL test databases and the test container were removed.

Next: 2f owns application schema evolution and upgrade paths, provider integration of the existing download startup gate, queued/active/lease/group reconciliation and stale-message/run-generation verification. Lite is not complete until those pass. No messaging, authentication, secrets, backup UI or deployment work was added.


## 2f handoff

Completed 2026-10-03. **Phase 2 is complete; Phase 3 is unblocked.** SQLite persistence and shared download restart reconciliation are verified. Ordinary Lite startup still requires the planned Phase 3 local infrastructure adapters; the transport guard remains and its message now identifies Phase 3.

Changed files: portable startup-table description; appended PostgreSQL M098 and checksummed SQLite v2 migration/runner/rebuild support; generation boundary persistence; shared download reconciliation and group-request acceptance; watchdog/timeout startup decorator and provider composition; startup service ordering/cancellation/orphan discovery; request/result handler test access; upgrade/restart tests, fixture hooks and updated foundation/composition assertions; plan/architecture/inventory and [schema/restart contracts](../src/App/DataBridge/Persistence/Schema/README.md). PostgreSQL M001–M097 and the reviewed SQLite v1 baseline/manifest/checksum are unchanged. Workflow schemas remain PostgreSQL library v0 / SQLite v1.

Upgrade decisions: supported SQLite application paths are fresh → v1 → v2, released v1 → v2 and repeated v2 initialization. All pending upgrade operations/history rows share immediate writer ownership and commit atomically; unknown/gapped/altered history fails clearly. Explicit rebuild descriptions preserve data and require full reviewed target constraints/indexes/triggers; FKs are disabled outside the rebuild transaction, checked before commit and restored on all exits. Full still runs FluentMigrator, now appending M098 to the unchanged original history. The historical exporter/baseline cutoff remains M097.

Restart decisions: resolving Cleipnir containers can start watchdogs before hosted services, so expiry/timeout recovery is gated at the store until reconciliation commits. The blocking startup service precedes every application consumer, and concurrent hosted-service startup is disabled. Typed panels delete unfinished known/orphan download flows and group flows while preserving import recovery. Reconciliation stops queued jobs/runs, fails active/historical unfinished runs and attempts, expires active leases and settles groups without implicit fresh runs. The transaction advances a durable generation fence, protecting equal timestamps, truncation and clock rollback. Stale requests/results are acknowledged without restarting invalidated work; stale group redelivery cannot stop explicitly restarted children. Explicit Start creates a fresh RunId; old dispatches/results and restart-invalidated expansion remain fenced.

Verification: six new shared restart cases pass on real-file SQLite and PostgreSQL 18.3, including the real startup service/containers, watchdog/timeouts, orphan deletion/import resumption, stale request/result handlers, queued/active/attempt/lease/group state, explicit fresh runs, worker-claim races and cancellation during held-writer contention. Four new upgrade tests cover both released provider paths plus SQLite rebuild integrity, rollback/cancellation/retry and history/concurrent initialization. Existing repository/contention/workflow/foundation suites run in the full verification, including the killed-process durable recovery test. Final unit suite: 543 total, 538 passed and the same five pre-existing failures documented above; all persistence cases passed with PostgreSQL enabled. Download repository integration suite: 24/24 passed. UnitTests, Lite Release and IntegrationTests builds passed with zero warnings/errors. Repeated Lite initialization-only startup preserved application v1/v2 and workflow v1 histories, with SQLite integrity checking clean. Baseline drift, generated inventory and whitespace checks passed.

Phase 3 handoff: initialize application and workflow histories before host startup; use the shared repositories, `ApplicationDatabase` and `ApplicationMutation`; preserve the provider store and watchdog/readiness gate; register dispatch/results/admin consumers after reconciliation; carry immutable RunId/stage/attempt/artifact/dispatch and generation checks through local messaging. See the linked contract note for storage/time/transaction/upgrade constraints.

Deferred issues: local infrastructure/messaging/search/dispatch remains Phase 3; authentication/secrets/backup UI/packaging remains Phase 4. PostgreSQL-to-SQLite data conversion remains unsupported. The five pre-existing unit failures from 2b–2e are unrelated to persistence. No later-phase implementation was pulled into this increment.
