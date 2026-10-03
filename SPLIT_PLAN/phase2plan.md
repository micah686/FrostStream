# Phase 2 Plan — SQLite Persistence

Phase 2 is split into reviewable increments so each can be completed and checked within a limited subscription window. Each increment should end with a working build, focused verification, and a short handoff note recording changed files, decisions, and remaining work. Keep Full on PostgreSQL throughout; Lite must not be presented as complete until Phase 2f is done.

## 2a — Inventory and persistence boundaries

**Status: complete (2026-10-03).** See the [verified source inventory](phase2-persistence-inventory.md), [adapter architecture and baseline decision](phase2-persistence-architecture.md), and [tracked checklist and handoff](phase2-checklist.md). Runtime behavior remains unchanged.

- Map the shared entities, repositories, direct SQL, provider-specific mappings, migration paths, and Cleipnir persistence used by the application.
- Identify PostgreSQL-only features in use: enums, JSON and arrays, timestamp semantics, bulk operations, upserts, locking, and transaction behavior.
- Define the smallest provider adapter boundaries needed to keep business rules and repository contracts shared. Record any operations that cannot be expressed portably.
- Document the current PostgreSQL migration history and select how a clean SQLite baseline will be created without rewriting Full's history.
- Add or update architecture notes and a tracked implementation checklist; do not switch runtime behavior yet.

**Completion:** There is a verified inventory, explicit adapter design, SQLite baseline approach, and ordered backlog for the following increments.

## 2b — SQLite connection and schema foundation

**Status: complete (2026-10-03).** See [baseline configuration and verification](../src/App/DataBridge/Persistence/Sqlite/Schema/README.md) and the [2b handoff](phase2-checklist.md#2b-handoff). SQLite remains opt-in and available for initialization only; Lite runtime is not complete.

- Add Lite SQLite configuration using `/data/frostreamlitedb`, with a configurable path for development and tests.
- Configure foreign keys, WAL, and bounded busy handling in one shared connection setup.
- Implement the initial SQLite schema/baseline for the core shared entities and indexes, keeping PostgreSQL migrations untouched.
- Add migration startup plumbing that selects the correct provider path from deployment mode.
- Keep Lite persistence opt-in until the baseline can initialize reliably.

**Completion:** A fresh SQLite database initializes repeatedly, has the expected tables/indexes, enforces foreign keys, and Full continues to use its existing PostgreSQL migration history.

## 2c — Core repositories and type mappings

- Port repository reads and writes for the core application entities to SQLite through the boundaries from 2a.
- Implement portable mappings for enums, JSON, arrays, and timestamp values used by those repositories.
- Keep shared repository interfaces and business rules; isolate provider-specific SQL and mapping details in adapters.
- Port ordinary transactions and upserts, documenting any SQLite-specific implementation choices.
- Add focused repository coverage that runs the same behavioral cases against PostgreSQL and SQLite where practical.

**Completion:** Core repository behaviors work on both providers, and Full's PostgreSQL behavior remains unchanged.

## 2d — High-contention and bulk operations

- Port bulk insert/update paths and any provider-specific batching.
- Replace PostgreSQL locking assumptions with SQLite-safe write coordination and short transaction scopes.
- Bound write contention and ensure cancellation, rollback, and retry behavior are consistent and observable.
- Exercise concurrent writers and verify that duplicate or retried operations do not corrupt data.

**Completion:** Bulk and concurrent-write paths pass focused checks on both databases without unbounded waits or partial writes.

## 2e — Durable Cleipnir workflow store

- Confirm the durable-store contract supported by the currently pinned Cleipnir version before implementation.
- Implement a SQLite-backed store compatible with that contract; keep the existing PostgreSQL-backed store for Full.
- Persist workflow state and required scheduling/recovery data using the shared workflow model.
- Verify workflow creation, progress, completion, cancellation, crash recovery, and schema upgrade behavior on both providers.

**Completion:** Both providers can resume durable workflows correctly, with restart behavior covered by focused tests.

## 2f — Upgrade path and restart reconciliation

- Complete shared schema evolution support where possible and isolate provider-specific migration operations where necessary.
- Verify fresh installs and upgrades from each supported SQLite baseline; retain and verify Full's existing PostgreSQL migration sequence.
- Implement and verify Lite download restart reconciliation: queued jobs stop, active jobs become interrupted/failed, and stale queue messages cannot restart invalidated runs.
- Run the shared repository and workflow suites against both databases, including contention and restart cases.
- Record deferred issues and hand off the persistence interfaces and constraints to Phase 3.

**Completion:** Both databases pass the same applicable repository and workflow checks, upgrades work, and Lite restart reconciliation is reliable. This completes Phase 2 and unblocks Phase 3.

## Suggested session boundaries

Treat each numbered subsection as its own work session. If a session ends early, leave the repository in a buildable state and update the checklist with completed items, exact blockers, and the next concrete task. Do not start a later subsection until its prerequisites are complete; in particular, 2c depends on the boundaries and schema from 2a–2b, 2e depends on stable persistence primitives, and 2f closes the integration work.

## Phase 2 scope guard

Phase 2 covers persistence and restart correctness only. Runtime messaging, local dispatch, authentication and authorization changes, secrets, backup UI, packaging, and deployment-profile work remain in Phases 3 and 4. This keeps each subscription-sized increment focused and prevents Phase 2 from absorbing the rest of the Lite refactor.
