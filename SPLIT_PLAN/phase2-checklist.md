# Phase 2 implementation checklist and handoff

Authoritative scope: [phase2plan.md](phase2plan.md). Design: [persistence architecture](phase2-persistence-architecture.md). Evidence: [source inventory](phase2-persistence-inventory.md).

## 2a — completed

- [x] Map shared entity declarations, EF configurations, repository/read contracts and raw SQL across modules.
- [x] Identify enum/schema, JSON, array, timestamp, dialect, upsert, bulk, lock and transaction dependencies, including unbounded timeouts.
- [x] Define provider composition/model, operation, atomic mutation and workflow retention boundaries; record nonportable operations.
- [x] Verify PostgreSQL source migrations 1–97 and separate Cleipnir schema ownership; select an independent SQLite baseline at the M097 application schema cutoff.
- [x] Record ordered implementation and validation tasks without changing runtime behavior.

## 2b — connection and schema foundation (next)

- [ ] Add provider registration/migration initialization boundaries; Full must retain its current PostgreSQL connection, migration scan and history.
- [ ] Add explicit Lite persistence opt-in and configurable SQLite path, default `/data/frostreamlitedb` (plan spelling is intentional).
- [ ] Centralize connection setup: foreign keys per connection, WAL, finite busy budget, cancellation; include requirements for later workflow connections.
- [ ] Build a disposable PostgreSQL database through M097 and export/review the final schema manifest (tables/columns/defaults/FKs/unique/check constraints/indexes/seeds). Reconcile EF and every raw SQL owner from the inventory.
- [ ] Implement independent SQLite baseline v1 and application version table with flattened names and agreed type encodings; include raw-only metadata, policy, watch/like, maintenance, circuit and statistics tables. Preserve only active seeds.
- [ ] Split shared EF model configuration from provider types/defaults and verify model cache behavior. Record enum label/GUID/time conversion rules used by both EF and raw commands.
- [ ] Validate initialization twice, schema manifest equivalence, expected indexes, unique/check constraints and FK rejection on every connection. Confirm Full migrations still run unmodified.
- [ ] Keep Lite opt-in/experimental; successful schema creation does not imply repository/workflow readiness.

## 2c — repositories and mappings (after 2b)

- [ ] Port ordinary EF repositories first: presets/config sets, schedules, creator discovery, imports, playlists and users/storage. Exercise direct-EF consumers too.
- [ ] Extract operation contracts for embedded SQL; retain shared handlers, DTOs and ownership/validation rules.
- [ ] Port metadata graph write/read, raw note search, rendition/encoding queries, account/caption/thumbnail reads and search hydration.
- [ ] Port watch/likes, policy persistence, deletion/cleanup, provider circuits, deduplication and statistics SQL through selected adapters. Keep optional ClickHouse persistence separate; port its application DB marker/backfill queries.
- [ ] Test native enum labels versus existing string enums; JSON/null/empty collections; GUID identity; UTC microsecond precision/pre-epoch/date semantics; generated IDs and deterministic aggregate/latest ordering.
- [ ] Verify ordinary transactions and all upsert conflict targets, COALESCE behavior, affected rows and atomic increments on both providers. Keep SQLite FK failures/unique conflicts observable.
- [ ] Reuse behavioral cases with real PostgreSQL and SQLite fixtures. Existing EF InMemory repository tests do not prove SQL, constraints or transaction portability.

## 2d — bulk and contention (after 2c)

- [ ] Port import batches (500), discovery upserts, set updates, metadata child replacement, playlist reorder and retention/delete batches; bound SQLite parameter counts.
- [ ] Implement atomic download mutation boundary preserving run/attempt/lease guards. SQLite must acquire writer ownership before read-modify-write; Full retains row locks.
- [ ] Use short database-only transactions, finite retry/busy budgets and cancellation. Preserve connection/transaction sharing between EF and raw operations.
- [ ] Exercise concurrent claim/start/stop/results, duplicate messages, conflicting reorder, concurrent upserts and purge/write races; prove rollback and no partial graph updates.
- [ ] Verify retries cannot repeat external side effects and do not hide non-contention errors; record retry/timeout outcomes. Review zero-command-timeout sites from inventory.

## 2e — durable workflow store (after stable persistence primitives)

- [ ] Inspect exact pinned Cleipnir/ResilientFunctions 4.2.5 store interfaces and schema initialization/versioning. Record the complete supported contract before writing the store.
- [ ] Implement all required SQLite workflow state, effects/messages, leases/epochs, timeout/scheduling and recovery primitives; no in-memory fallback.
- [ ] Retain PostgreSQL UsePostgresStore and shared flows/NodaTimeFlowSerializer; initialize SQLite workflow schema independently of application baseline history.
- [ ] Replace terminal/orphan discovery SQL in both purgers with the workflow retention query boundary; keep actual instance deletion through Cleipnir control panels.
- [ ] Verify creation, message/effect round trips, progress, completion, cancellation, crash recovery and workflow schema upgrades against both stores.
- [ ] Distinguish recoverable import workflows from intentionally interrupted download runs on startup.

## 2f — upgrades and restart closure (after 2b–2e)

- [ ] Add portable evolution descriptions where possible and separate provider migration implementations where necessary; test data-preserving SQLite rebuilds and independent histories.
- [ ] Verify fresh/upgrade paths for each supported SQLite version and Full's unchanged PostgreSQL sequence.
- [ ] Port/verify the existing blocking DownloadFlowStartupService/ReconcileForStartupAsync behavior: queued stopped, active interrupted/failed, groups settled, leases expired, old flows invalidated before ingress/results.
- [ ] Verify immutable RunId/AttemptId and generation gates reject stale queued/result messages after restart; cover cancellation and restart during mutation.
- [ ] Run all applicable shared repository/workflow behaviors on both providers including contention, restart and retention.
- [ ] Record deferred issues and Phase 3 persistence handoff. Only then mark Phase 2 complete.

## 2a handoff

Changed files: this checklist, `phase2-persistence-architecture.md`, generated `phase2-persistence-inventory.md`, `tools/persistence_inventory.py`, and completion links in `phase2plan.md`.

Decisions: retain shared models/contracts/business logic; isolate provider mappings and operation SQL; combine locking with atomic mutation; abstract workflow retention queries; baseline SQLite independently at Full's M097 application schema. Runtime registration, packages and PostgreSQL migrations are unchanged.

Verification commands: `python3 SPLIT_PLAN/tools/persistence_inventory.py --check`, `git diff --check`, and `dotnet build src/App/Lite/Lite.csproj --no-restore`. Result: build passed with 0 warnings and 0 errors; inventory drift and whitespace checks passed. Documentation link targets and the 45 DbSet/23 EF enum counts were checked against source.

Remaining evidence: live PostgreSQL catalog manifest in 2b; SQLite query/type/constraint equivalence in 2c–2d; exact pinned Cleipnir durable-store contract in 2e; cross-provider upgrades/restart correctness in 2f. These are later acceptance checks, not claims made by 2a. No current blocker to beginning 2b; its first concrete task is provider registration plus manifest capture. PostgreSQL-to-SQLite data conversion remains deferred; Phases 3–4 infrastructure stays out of this increment.
