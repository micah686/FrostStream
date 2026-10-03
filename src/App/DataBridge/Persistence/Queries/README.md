# Application SQL adapters (Phase 2c)

Shared repositories and consumers select an operation through `ApplicationDatabase.Sql`, an existing connection's `Sql`, or EF's `ParameterizedSql`. The catalog contains 141 statement/fragment pairs, grouped by operation family. PostgreSQL text was copied verbatim from the existing application operations; each SQLite statement is explicit. There is no SQL dialect rewriting at runtime. Repository interfaces, validation, ownership rules, DTOs and business transitions remain shared.

Raw commands use `ApplicationDatabase` for standalone connections or `ApplicationDbCommands` for a transaction already owned by EF/the caller. The adapter selects the registered PostgreSQL data source or SQLite connection factory. Standalone SQLite commands own and dispose their connections; transaction commands leave ownership with the caller. A requested zero command timeout keeps PostgreSQL's existing behavior and uses SQLite's configured finite busy budget. SQLite I/O remains synchronous; the busy budget is not an overall query execution deadline.

`Sql` arguments are **trusted SQL fragments/identifiers selected by application code**, such as a controlled WHERE clause or taxonomy column. Request data must use command parameters or `ParameterizedSql` arguments. Dynamic qualified taxonomy table names are checked against the application manifest before being mapped. `{fsN}` placeholders are catalog template slots, not a SQL parser.

The shared parameter and reader adapters use the 2b encodings: lowercase GUID `N` text, native enum labels, JSON text, ISO date text and integer UTC microseconds. Existing string enums retain their original case. Nullable values remain SQL NULL. PostgreSQL continues to bind native UUID/array/timestamp types. SQLite array parameters are JSON arrays consumed by `IN (SELECT value FROM json_each(@parameter))`, including empty arrays. This supersedes 2a's proposed expanded IN lists: one bound parameter avoids SQLite's parameter-count limit and preserves typed membership. The JSON array is a query parameter, not an application array column. Aggregate results are decoded separately.

SQLite connections register `fs_ilike`, `fs_guid_text` and `fs_epoch_round` for search, UUID display and PostgreSQL's rounded integer epoch projections. EF translates the corresponding shared predicates to native PostgreSQL expressions. SQLite search handles Unicode scalars, wildcard escaping, case folding, NULL and long patterns; locale-specific PostgreSQL collation behavior is not replicated. UTC history buckets floor negative microseconds before date conversion and preserve month-end interval behavior. JSON aggregation, enum caption ordering, stream selection and newest-content selection have explicit SQLite queries.

Ordinary metadata replacement shares one EF transaction with raw commands. Policy replacement, playlist shifts and content reservation retain transaction boundaries. Media deletion explicitly wraps policy cleanup and root deletion in one transaction because SQLite does not automatically make a multi-statement command atomic. `FOR UPDATE` remains in PostgreSQL; SQLite omits it inside the existing EF immediate writer transaction. This does **not** claim concurrent download mutation safety: read-before-transaction paths, contention/retries and bulk operations are 2d. Uniqueness classification is isolated in `ApplicationDatabaseErrors`; other database errors remain observable.

Optional ClickHouse replay queries stay separate. Only application database live-chat marker and backfill SQL is adapted here. Both workflow-aware purgers retain PostgreSQL SQL until the 2d batch and 2e retention/store boundaries exist. Normal SQLite runtime remains gated through 2f; this increment does not install an in-memory or mixed-provider workflow fallback.

## Verification

The focused suite always uses real SQLite files. To run the same behavioral cases on PostgreSQL, provide a **disposable** server connection through `FROSTSTREAM_TEST_POSTGRES`. The fixture creates and drops a unique database per case; the account needs permission to do so. It runs Full's unchanged FluentMigrator history through 97. For example:

```bash
FROSTSTREAM_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=disposable-test-password' \
  dotnet run --project Tests/UnitTests/UnitTests.csproj -- \
  --treenode-filter '/*/*/CoreRepositoryPersistenceTests/*'
```

Without that variable, the suite runs SQLite only. Fifteen behavioral cases run on both providers when configured; a sixteenth test prepares the SQLite catalog against the complete baseline. Coverage includes metadata/technical/caption/comment replacement and rollback; generated IDs, account aliases and assets; search hydration and empty collections; native caption enum ordering; notes/ownership; policies/version checks/unique conflicts; watch/likes; presets/config sets/schedules/discovery; scoped user/storage consumers; ordinary playlists; download creation/dedup/circuits/content reservation; encoding/rendition/latest-version reads; statistics increments and UTC/pre-epoch buckets; stale cleanup and optional live-chat markers.
