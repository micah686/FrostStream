# Application mutations and bulk operations (Phase 2d)

`ApplicationMutation.MutateAsync` owns a complete database-only operation. Shared repository wrappers enter it before reading state. Nested repository calls join its transaction; EF and raw commands use the same connection and transaction. SQLite acquires `BEGIN IMMEDIATE` before those reads. PostgreSQL uses serializable transactions and retains job-row `FOR UPDATE`; serialization failures replay the complete operation using fresh tracked state. Coordination happens in the database, across independent connections and processes.

Use a scoped DbContext without pending caller changes. Save those changes first; the boundary rejects them without discarding them. Each relational attempt clears previously tracked snapshots. Callbacks must dispose their commands/readers, keep all database writes inside the boundary, and perform no network, filesystem, message dispatch or other external side effects. Caller-owned transactions cannot be replayed safely and are rejected. The one-statement processed-message insert can join a caller-owned transaction because it needs neither a state read nor a notification.

`AfterCommitAsync` queues best-effort statistics and notifications. Only callbacks from the committed attempt run, after disposing its transaction and outside the retry loop. A callback failure never replays the committed database operation. These callbacks retain their existing best-effort contract; they are not a durable outbox.

## Limits and error handling

| Boundary | Limit |
| --- | --- |
| SQLite writer acquisition | One second per synchronous acquisition; check cancellation between waits. Open the EF connection before setting that limit because the connection interceptor configures its timeout. |
| SQLite connection pragma setup | One-second busy limit; foreign keys, WAL and connection functions still apply on every open. |
| SQLite contention replay | Configured `Persistence:Sqlite:BusyTimeoutSeconds` (default 5, allowed 1–60), at most eight attempts, with small bounded jitter. An in-progress acquisition can finish after the replay budget expires. |
| PostgreSQL mutations | Transaction-local 1-second lock timeout, 5-second statement timeout, default 5-second replay budget and at most eight attempts. These do not change global PostgreSQL settings. |
| Raw SQLite commands | Zero or larger requested busy timeouts are capped at the configured connection budget. This is a busy budget, not a query execution deadline. |
| Import writes | Existing 500-item flushes; the complete submitted scan and its counters commit atomically. A later failed flush rolls back earlier flushes. |
| Membership queries and retention writes | Distinct IDs in batches of 400, leaving room under a 999-variable SQLite build. Empty ID sets issue no membership query. |
| Retention selection / expired leases | At most 500 roots/leases per selection; stale-media deletion uses 400 roots per transaction. |
| Maintenance / workflow discovery | Vacuum/reindex commands use 300 seconds on PostgreSQL and the configured busy budget on SQLite. Remaining PostgreSQL workflow discovery uses 15 seconds. Shutdown cancellation is passed to maintenance commands. |

Retry classification unwraps EF's provider exceptions and accepts SQLite BUSY/LOCKED or PostgreSQL serialization/deadlock/lock-timeout errors. PostgreSQL can also report a competing natural-key insertion as a unique violation; only explicitly named keys in operations that already promise identity reuse are replayed. Plain creation, other unique conflicts, FK/check failures, validation errors and unknown/transport failures remain visible. Exhausted contention propagates the provider exception. Cancellation rolls back and is checked again after a synchronous SQLite wait.

The `FrostStream.Persistence` meter exposes `persistence.mutation.retries`, `.timeouts`, `.cancellations` and `.duration` (milliseconds), tagged by operation and provider. The duration includes contention replay for the database operation; post-commit callbacks are excluded.

## Shared behaviors

Downloads now read job/run/attempt/lease state and write their changes inside one boundary, including initial requests, attempts, lease renewal/expiry, warnings and artifacts. Existing run, stage, attempt and dispatch guards remain shared. Attempt redelivery does not reset a claimed attempt to Pending; obsolete run artifacts and leases cannot overwrite or fail a fresh run. Finalization keeps its attempt, source mapping, playlist membership, job and run atomic against Stop. Group Start/Stop dispatches separate guarded child mutations instead of holding one writer transaction around every child.

Discovery batches and their scan state, import bulk/set updates and counters, metadata child replacement, source/content reservation and playlist shifts/reorder use the same boundary. An import session is locked before reading its existing paths on PostgreSQL, preventing a concurrent scan from inserting a path from an old snapshot. Playlist staging deduplicates entries within the submitted batch.

`ApplicationRetention` selects bounded application candidates and rechecks age, terminal status, sibling state, active leases and drained-group conditions inside the delete transaction. Cleanup removes dependent history only for jobs it actually deletes. It preserves durable playlist/library rows. Both purgers delegate their application operations to this boundary, while workflow instance lookup remains PostgreSQL pending 2e. Flow panel deletion remains outside application transactions and skips live panels.

Media file deletion still happens once through the Worker before the database operation. The database callback rechecks active downloads and compares the stored file set with the deleted snapshot. A concurrent change returns a conflict with the actual file-delete count and preserves rows for retry, including newly added objects. Physical deletes cannot be rolled back. Search and ClickHouse cleanup run after a successful database deletion and are never part of a database retry.

## Verification

Run `BulkContentionPersistenceTests` with the same optional disposable PostgreSQL configuration as the [2c suite](Queries/README.md):

```bash
FROSTSTREAM_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=disposable-test-password' \
  dotnet run --project Tests/UnitTests/UnitTests.csproj -- \
  --treenode-filter '/*/*/BulkContentionPersistenceTests/*'
```

Seventeen cases run against both real providers; one additional SQLite case verifies held-writer timeout metrics. Cases cover redelivery, competing starts/claims, lease regrant/heartbeat/expiry, stale results, Stop/finalization, content/source identity races, conflicting playlist changes, 1,201-item import rollback/deduplication and a reduced SQLite variable limit, discovery/metadata replacement, cancellation, whole-operation replay without repeated callbacks, media/retention races and bounded cleanup. Fixtures use independent actor connections, disable pooling for temporary PostgreSQL databases, and limit simultaneous cases to fit a stock disposable server.

SQLite runtime remains gated pending the durable workflow store in 2e and upgrade/restart closure in 2f. PostgreSQL migration source and the SQLite v1 baseline are unchanged.
