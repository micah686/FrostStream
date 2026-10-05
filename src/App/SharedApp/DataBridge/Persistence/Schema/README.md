# Schema evolution and download restart closure (Phase 2f)

Phase 2 persistence is complete. Full uses PostgreSQL and its existing Cleipnir store; Lite can initialize and use SQLite persistence and the shared startup/repository/workflow code. Ordinary Lite host startup still requires **Phase 3's local infrastructure adapters**. The transport guard remains; no NATS-backed fallback is installed for Lite.

## Independent histories and upgrades

| History | Current version | Upgrade contract |
| --- | --- | --- |
| Full application | FluentMigrator M098 | M001–M097 are unchanged; M098 adds `jobs.download_startup_state`. Fresh installs replay the complete history; existing M097 installs append M098. |
| SQLite application | 2 | Immutable v1 baseline represents the reviewed M097 catalog. The new checksummed v2 step adds `jobs_download_startup_state`. Fresh installs apply v1 then v2; existing v1 installs preserve their data/seeds and append v2. |
| Full workflows | Cleipnir library version 0 | The pinned 4.2.5 library owns initialization/migration. No older upgrade scripts exist in that version. |
| SQLite workflows | 1 | The independent 2e initializer handles empty-to-v1 initialization and repeats without replacing state. |

`SchemaTable`/`SchemaColumn` describe the portable startup table once; PostgreSQL M098 and SQLite v2 render their own names and timestamp storage from that description. PostgreSQL enum/DDL history and SQLite baseline/rebuild operations remain provider-specific. There is no attempt to translate all FluentMigrator operations or reuse PostgreSQL version numbers in SQLite.

SQLite initialization validates the baseline checksum and the complete ordered, contiguous upgrade history. Future/unknown versions, gaps and changed checksums fail before upgrades run. Released steps are append-only. Each upgrade identity includes its version, name and rendered SQL; changing a released definition or renderer must not silently rewrite it. Application and workflow histories are never substituted for each other.

All pending SQLite upgrade DDL/data copies and history rows commit together under immediate writer ownership. An interrupted upgrade leaves the last committed version, and startup cannot proceed until initialization succeeds. Fresh baseline DDL/seeds/history also remain atomic. Concurrent initializers either observe the completed history or fail within the shared finite busy budget; they never duplicate seeds or versions. Applied timestamps use the shared clock.

`SqliteSchemaMigrations.Rebuild` supports explicit data-preserving table rebuild descriptions: full replacement DDL, preserved columns and every required index/trigger. It does not rewrite arbitrary SQL or guess the target schema. Rebuilds temporarily disable FKs on their dedicated connection **before** the transaction so dropping the parent cannot cascade away child rows. A full `foreign_key_check` runs before commit, and FK enforcement is restored in `finally`, including failure/cancellation. Ordinary migrations keep FK enforcement enabled. Tests exercise a real parent/child rebuild with checks/indexes, rollback and retry; no unnecessary production table rebuild is shipped in v2.

The schema exporter and immutable baseline manifest deliberately retain the M097 cutoff. Exporting a current M098 database as if it were the old baseline is rejected. Use a disposable database migrated only through M097 when reproducing that historical manifest; future SQLite evolution belongs in appended upgrade steps.

## Startup and stale work

`DownloadFlowStartupService` is the blocking gate on **both** providers. It runs before application consumers, with concurrent hosted-service startup disabled. Host construction can resolve flow containers and start Cleipnir watchdogs before hosted-service `StartAsync` calls; `StartupGatedFunctionStore` therefore hides expired workflows and due timeouts until reconciliation is ready. Import recovery is released only after download invalidation commits; there is no guessed watchdog startup delay.

Startup deletes known unfinished download-run flows and group flows through their typed control panels. It also discovers/deletes unfinished download/group instances with no corresponding application row. Terminal job flows remain available to retention; import flows are preserved. Cancellation is checked between deletions, and failure keeps recovery gated. Workflow operations use the pinned interfaces' bounded busy handling (those interfaces have no cancellation-token parameters).

The shared repository then reconciles in one database-only mutation:

| Recorded work | Restart result |
| --- | --- |
| Queued jobs/runs | Stopped; queued jobs record `service_restarted_before_start`. |
| Running, stopping or compensating jobs/runs | Failed with `FailureKind.Interrupted` and `service_restarted`. Historical unfinished runs are settled too. |
| Pending, running or retry-waiting attempts for interrupted runs | Failed/interrupted with an end timestamp. |
| Active leases | Expired with a release timestamp; stale dispatch IDs cannot claim or complete work. |
| Unfinished groups | Failed/interrupted; aggregate refresh cannot reopen them merely because child statuses changed. |
| Terminal jobs/runs | Preserved; no replacement run is created implicitly. |

The repository advances the singleton download generation boundary in the same transaction. The boundary is the next microsecond after the current clock, or one microsecond after the persisted previous boundary, whichever is later. This prevents timestamp truncation, equal-clock restarts and clock rollback from admitting old queued requests. Readiness is published only after the committed result returns. With a clock rollback, requests remain stopped until timestamps reach the boundary; explicit user Start can still create a new immutable run.

Old download requests are acknowledged and represented as stopped without a run. Old direct-group requests create a stopped group/child only when the group is absent; redelivery cannot stop a child/group explicitly started in the new generation. Group acceptance is an atomic shared repository operation, with retries limited to its known natural-key races. Existing terminal or mismatched group identities are rejected. Restart-invalidated expansion remains fenced even when a user explicitly restarts individual children.

Worker results continue through the existing job/run/stage/attempt/artifact/dispatch/lease guards. A stale result is acknowledged without a workflow message or history write. User Start creates a new `RunId`; previous dispatches and results cannot advance it. Producer/consumer acknowledgment behavior is tested through the real application handlers with the transport boundary substituted; transport replacement remains Phase 3.

## Verification and Phase 3 handoff

From the repository root, build `Tests/UnitTests/UnitTests.csproj` and execute its built test assembly. `FROSTSTREAM_TEST_POSTGRES` enables the same persistence cases against a disposable PostgreSQL server; otherwise SQLite cases still run. Focused classes are `CoreRepositoryPersistenceTests`, `BulkContentionPersistenceTests`, `WorkflowPersistenceTests`, `SqlitePersistenceFoundationTests`, `PersistenceUpgradeTests` and `DownloadRestartPersistenceTests`.

The new tests cover upgrades from released SQLite v1 and Full M097 with application/workflow state and seed edits preserved; fresh/current installs; rebuild checks/FKs/indexes; changed/gapped/future histories; concurrent initialization; migration rollback/cancellation/retry; the real flow startup gate with expired work and timeouts; orphan flow deletion; resumed import work; queued/active/lease/attempt/group reconciliation; old request/result handlers; fresh immutable runs; worker-claim races; and cancellation during writer contention. The 2e killed-process test remains in the full shared suite.

Phase 3 should use `ApplicationDatabase`, shared repositories and `ApplicationMutation`, the selected durable workflow store, and the blocking startup gate rather than introducing another persistence path. Initialize both histories before starting the host. Register download ingress/results/admin/lease consumers after reconciliation; keep watchdog recovery gated until readiness. Preserve immutable execution IDs and generation guards when replacing durable messaging and dispatch.

Deferred work remains as planned: local transport/dispatch/search and optional-integration adapters in Phase 3; authentication, secrets, backup UI and deployment packaging in Phase 4. PostgreSQL-to-SQLite data conversion is not a supported upgrade path. The five pre-existing unit failures recorded in the earlier handoffs remain outside persistence; see the 2f handoff for final results. A completed SQLite persistence layer does not mean the entire Lite product is deployable yet.
