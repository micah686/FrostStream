# SQLite application baseline v1

The baseline represents Full's application catalog after unchanged PostgreSQL migrations 1–97. Capture used a fresh, disposable PostgreSQL 18.3 database; the normalized manifest has 82 application tables, 66 foreign keys, 16 application check constraints and 96 non-primary indexes. All 45 EF entity tables and their column names match the catalog. The remaining tables include metadata graphs, policies, watch/likes, statistics, provider circuits and live-chat markers. Removed maintenance features and Cleipnir runtime tables are absent from the final application catalog.

`001-baseline.sql` is embedded alongside `postgres-v97-manifest.json`. SQLite applies it with the seeds and a row in `froststream_schema_versions` in one immediate writer transaction. The version is **1**, independently of PostgreSQL's version **97**. Repeated initialization preserves existing data and edits. Unknown/newer histories, a changed baseline checksum and unversioned databases containing tables are rejected. Version tracking does not repair a manually damaged schema. Future schema changes require appended migrations (2f); do not edit a released baseline.

## Initialize an experimental Lite database

From the repository root:

```sh
dotnet run --project src/App/Lite/Lite.csproj --no-launch-profile -- \
  --Deployment:Mode=Lite \
  --Persistence:Sqlite:Enabled=true \
  --Persistence:Sqlite:Path=/tmp/froststream-lite/core.sqlite \
  --Persistence:InitializeOnly=true
```

Run the same command again to verify repeatability. `Persistence:Sqlite:Path` defaults to `/data/frostreamlitedb`; relative paths resolve against the host content root. It must name a persistent file, not `:memory:` or a SQLite URI. `Persistence:Sqlite:BusyTimeoutSeconds` defaults to 5 and accepts 1–60. The same keys can be supplied with environment variables, e.g. `Persistence__Sqlite__Enabled=true`.

Initialization-only composition registers the selected database, runs migrations and exits without starting application services or an HTTP listener. It works with Full/PostgreSQL too, which is useful for disposable catalog capture. Normal startup remains on PostgreSQL unless SQLite is explicitly selected. Selecting SQLite in Full is a configuration error. Normal SQLite runtime startup is rejected until the repository/workflow/restart work is ready; database initialization is the scope of 2b.

## Connection and storage rules

Raw connections use `SqliteConnectionFactory`. EF opens and reopens use its setup through `SqliteConnectionSetupInterceptor`. Both enforce foreign keys, private cache, WAL and a finite Microsoft.Data.Sqlite command timeout. A 100 ms native busy wait complements the provider's timeout-bound retry loop; it does not replace that command timeout. Pooling is disabled for the foundation so connection ownership and setup are explicit. Cancellation is checked at opening and migration boundaries; an executing synchronous SQLite command can still consume its finite busy budget. This follows the provider's [connection configuration](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings) and [async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).

- Logical `schema.table` names become `schema_table`. Explicit index names also get the schema prefix. PostgreSQL primary-key indexes are represented by SQLite primary keys; non-primary unique indexes preserve unique constraints without duplicate SQLite autoindexes.
- Native enums use Npgsql's field-name translator/PgName rules, checked against catalog labels before building the SQLite EF model. Existing CLR string enum conversions stay unchanged. No ordinal mapping for native enums is introduced.
- GUIDs are lowercase 32-hex TEXT. JSON is TEXT with `json_valid` checks. VARCHAR limits, enum membership, booleans and GUID format are enforced with SQLite checks in addition to translated application checks.
- Instants are signed INTEGER Unix microseconds, floored for values between microsecond boundaries, including before the epoch. Metadata timestamps without a PostgreSQL timezone are mapped to the same integer UTC representation for future raw adapters. Date-only values remain ISO date TEXT. Repository/query behavior beyond these foundation mappings is 2c.
- Database-generated time defaults use integer epoch arithmetic at SQLite's millisecond clock resolution. Application-supplied values retain microsecond precision. These defaults do not round-trip all of PostgreSQL's generated microsecond clock precision; callers needing it must supply a shared-clock value.
- Active seeds include default local storage and ten schedules. Captured numeric IDs are retained; creation/update times are supplied by `IClock`, and next-due times are computed from each seed's Quartz cron and timezone. No timestamp from the disposable database is shipped. Backup schedule adaptation remains later scope; these schedules cannot execute through the initialization-only host.

## Reproduce and check the artifacts

First initialize an **empty disposable PostgreSQL database** with `Persistence:InitializeOnly=true`, `Deployment:Mode=Full` and its `ConnectionStrings:froststreamdb`. Do not use a production database. Then configure psql's `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, and authentication environment for that disposable database:

```sh
python3 SPLIT_PLAN/tools/export_postgres_manifest.py --output /tmp/postgres-v97-manifest.json
```

The read-only exporter queries `postgres_schema_manifest.sql`, verifies the cutoff and rejects unexpected non-seed data. PostgreSQL NOT NULL catalog entries are normalized to each column's nullable flag. Sequence identities, enum labels, primary/unique/check/FK constraints, index expressions/filters and active seed values are captured. Review changes against the checked-in manifest before replacing it. The SQL generator rejects unreviewed types/defaults/check syntax/index expressions instead of guessing dialect translations.

```sh
python3 SPLIT_PLAN/tools/sqlite_baseline.py --check
python3 SPLIT_PLAN/tools/persistence_inventory.py --check
dotnet run --project Tests/UnitTests/UnitTests.csproj -- \
  --treenode-filter '/*/*/SqlitePersistenceFoundationTests/*'
```

Use `sqlite_baseline.py` without `--check` only while authoring/reviewing a baseline. An unchanged cutoff does not authorize silently changing a released SQLite v1. Cross-provider repository equivalence, workflow durability and upgrades remain 2c–2f acceptance work.
