# PgBackRestWrapper

A dependency-free .NET 11 library for on-demand pgBackRest operations in a container. It assumes installed PostgreSQL tools, pgBackRest, mounted PGDATA and a mounted filesystem repository. It does not provide a scheduler, background worker, frontend, recovery console, deployment, or HTTP API.

```csharp
using PgBackRestWrapper;

var options = new WrapperOptions
{
    Stanza = "main",
    DataPath = "/var/lib/postgresql/data",
    RepositoryPath = "/backups/pgbackrest",
    StatePath = "/backups/wrapper-state",
    ConfigPath = "/etc/pgbackrest/pgbackrest.conf",
    SocketPath = "/var/run/postgresql"
};
var client = new PgBackRestClient(options);
// Optional: create managed config once. Fails if the file already exists.
await client.WriteConfigurationAsync();
await client.BootstrapAsync(); // checks binaries, creates stanza, runs archive check
var output = new Progress<OutputLine>(line => Console.Write(line.Text));
var backup = await client.BackupAsync(BackupType.Full, "before-import", output);
await client.BackupAsync(BackupType.Differential, progress: output);
await client.BackupAsync(BackupType.Incremental, progress: output);
Console.WriteLine(backup.Produced.Label); // actual backup type is also returned
var inventory = await client.GetRepositoryAsync();
await client.VerifyAsync(backup.Produced.Label, output); // omit label for entire repo
var health = await client.GetHealthAsync();
await client.ExpireAsync(); // applies dependency-aware configured retention
```

Use either the generated config or [examples/pgbackrest.conf](examples/pgbackrest.conf). An existing custom config is authoritative; `WrapperOptions` generates config but does not rewrite it or override environment settings. Keep paths, connection settings and retention synchronized with the options. Backups explicitly select online mode, archive checking and zstd. Runtime tuning in an existing config takes effect in pgBackRest. Do not use additional repositories/PG hosts or restore overrides with this single-local-repository wrapper. Control `PGBACKREST_*` environment variables and configuration includes in the container.

Run as the PostgreSQL operating-system user, with ownership/access to data, repository, state, config, socket and scratch volumes. PostgreSQL needs the same pgBackRest configuration and repository access for `archive_command`. Keep state outside the repository and PGDATA (a sibling directory on the backup mount is fine). State contains SQL, command arguments and diagnostics; protect its permissions. Put credentials in `.pgpass`/`PGPASSFILE` or pgBackRest's supported secret configuration, never in SQL or backup names. Use matching PostgreSQL major-version binaries and extensions. Scratch startup needs `postgres`, `pg_ctl` and `psql` on PATH; `pg_dump` is required only for logical exports.

The [PostgreSQL configuration](examples/postgresql.conf), [HBA](examples/pg_hba.conf), and [identity map](examples/pg_ident.conf) show required archiving and socket settings. pgBackRest backup/restore uses physical cluster backups; it is not limited to one database. `archive_timeout=60s` is an illustrative policy, not an RPO guarantee. PostgreSQL must be running for bootstrap's archive check and online backups.

## Restore and validate

Stop PostgreSQL using the owner's normal mechanism. Prevent concurrent restarts or writes for the entire restore. Preserve and independently check a rollback copy of populated data before proceeding. The wrapper cannot fence another container or certify a caller-provided rollback copy.

```csharp
var request = new RestoreRequest
{
    Destination = options.DataPath,
    ConfirmStanza = "main",
    ConfirmServerStopped = true,
    BackupLabel = backup.Produced.Label,
    Mode = RecoveryMode.Consistency,
    Delta = true,
    // Required if Destination is populated. Must already contain PG_VERSION/base.
    PreservedRollbackDirectory = "/rollback/cluster-before-restore"
};
var receipt = await client.RestoreAsync(request, output);
// receipt.Stage == FilesRestored. Start PostgreSQL separately, then observe it:
receipt = await client.ObserveRecoveryAsync(receipt.Id, options.SocketPath, options.Port,
    [new SqlAssertion("postgres", "SELECT 1;", "1")]);
```

`Delta` checks existing files; it can replace/remove files to match the recovery state. Empty destinations work with or without delta. Populated destinations require delta, exact stanza confirmation, stopped-server attestation and an existing rollback directory. PID files (even stale ones), symlink destinations/ancestors and existing nested symlinks are refused. The caller must resolve stale PID files, real ownership, capacity for the full expanded cluster, and external mount fencing. `MinimumFreeBytes` is a configurable floor, not a full cluster-size estimate. Existing symlinked tablespaces require a fresh destination. All restored tablespaces map under `Destination/wrapper-tablespaces`, and other original symlinks restore as regular files/directories.

Before starting an alternate-destination restore, disable its archiving and isolate its sockets/TCP settings so it cannot write WAL into the source repository or accept application traffic. The scratch API applies these settings automatically.

Change `Mode` for other recovery choices:

| Mode | Options | Behavior |
| --- | --- | --- |
| Consistency | Optional `BackupLabel` | `--type=immediate`: consistency of selected/latest set |
| Latest | Optional `BackupLabel` | `--type=default`: replay to end of available archive stream |
| Timestamp | `Timestamp = DateTimeOffset.Parse("2026-11-01T12:00:00Z")` | Latest eligible backup ending before UTC target, or validate explicit selected set |
| RestorePoint | `BackupLabel`, `Target = "before_import"` | Recover to named restore point from explicit preceding set |
| Lsn | `Target = "0/4000120"`, optional `BackupLabel` | Select set with stop LSN at or before target |

`Timeline` accepts `latest`, `current` or a positive timeline number. `Exclusive` is supported for timestamp/LSN targets. Backup metadata cannot prove uninterrupted WAL coverage, exact recoverable times or the existence of a future named point; unknown coverage is explicitly reported. Missing WAL or an unreached target must be resolved during PostgreSQL recovery.

Timestamp/name/LSN recovery pauses at the target. The observer requires paused recovery and matching target settings before reporting `TargetReached`. Consistency/latest require PostgreSQL to have left recovery. The observer verifies the SQL endpoint's data directory. It records `RecoveryPending` before attempting SQL; unavailable/recovering servers never become validated. The observation assumes no operator manually pauses recovery or substitutes a cluster at that path; inspect PostgreSQL recovery logs for independent proof. Assertions must pass to report `Validated`. Statements run as the configured user and must be trusted read-only checks. Validation is only as strong as the supplied assertions. The wrapper never promotes a paused target; review data, then promote using PostgreSQL's normal tools. After recovery, preserve history, restore intended config, and take a new full backup. Latest means end of available archives; it cannot establish whether unarchived source transactions were lost.

On-demand scratch testing leaves live PGDATA untouched:

```csharp
var tested = await client.TestRestoreAsync(new ScratchRequest(
    "/scratch", backup.Produced.Label,
    ExpectedDatabases: ["app"],
    Assertions: [new SqlAssertion("app", "SELECT count(*) FROM important_table;", "42")]), output);
```

Each scratch test uses a unique directory/socket, immediate consistency recovery, no TCP, disabled archiving and a private local HBA. Source auto.conf is reduced to restore-generated recovery options. The test checks expected databases have user tables, runs assertions, stops PostgreSQL, and cleans its own directory. If shutdown fails the directory is retained. Failure diagnostics include PostgreSQL's log. Scratch paths cannot overlap data/repository/state/config and must avoid spaces/quotes/backslashes because `pg_ctl -o` uses its own argument parsing. Run scratch tests in the same UID and compatible image; external PostgreSQL config is not guaranteed to be included in physical backups. Scratch tests can also select `Mode`, `Timestamp`, `Target`, `Timeline`, and `Exclusive` on `ScratchRequest` to exercise a specific recovery target. A successful drill demonstrates that target against the repository as inspected during that run; it does not establish archive-wide coverage or certify application correctness beyond assertions.

## Other operations and reporting

- `CreateRestorePointAsync("before_import")` creates an on-demand PostgreSQL named point and returns its LSN. PostgreSQL must be a writable primary with suitable privileges; archive the resulting WAL before relying on it.
- `GetHealthAsync()` records backup age, repository metadata state, storage capacity, `.ready` backlog, and archiver counters/failure state/segment-switch policy. `queryPostgres: false` permits offline repository observations but does not certify archive health. Historical failures and WAL endpoint metadata are not proof of archive continuity. `CheckAsync()` runs pgBackRest's active archive/config check.
- Bootstrap records actual binaries and probes help for required commands and `verify --set`. Bootstrap requires verification support; scoped verification is rejected if absent. Pin and test your chosen image/tool versions externally.
- Commands have bounded runtime, cancellation, process-tree termination and stdout/stderr capture (last 16 MiB of each). `IProgress<OutputLine>` streams timestamped output chunks; they are activity evidence, not an invented percentage. Large JSON info beyond that bound fails parsing rather than returning an incomplete inventory.
- State files store version probes, command outcomes (including verification scope, timestamps and diagnostics), backup outcomes, restore receipts, scratch results/failures and health. Reports use atomic replacement and file flush. The host owns retention of report files. A process crash before a command returns can leave no final report; do not infer success from its absence.
- A filesystem lease serializes mutating operations across clients sharing `StatePath`; another client fails fast on contention. Archive calls bypass this lease. pgBackRest's own locks remain active. This is not a durable job queue.
- `CompressionLevel`, `ProcessCount`, `Bundle` and `BlockIncremental` generate tuning settings. Blocks require bundling. Bundling/block storage is separate from requesting `BackupType.Incremental`. Bandwidth throttling has no general pgBackRest local-filesystem option; hosts can supply an `ICommandRunner` with their resource-control policy. The built-in runner does not claim to enforce a bandwidth cap. Maintenance windows are outside this library's scope.

See [IMPLEMENTED.md](IMPLEMENTED.md) for requirement coverage and remaining validation limits.

## Containerized tests

Run PostgreSQL and the test suite together without installing their tools on the host:

```sh
docker compose up --build --abort-on-container-exit --exit-code-from tests
```

The test image includes .NET 11, PostgreSQL tools and pgBackRest. It runs unit tests
and live backup/recovery checks against a separate PostgreSQL container using
shared data, repository and socket volumes. See [docker/README.md](docker/README.md)
for logs, retained evidence and resetting test volumes before another run.

## Validation

```sh
dotnet build PgBackRestWrapper.slnx
dotnet run --project PgBackRestWrapper.Tests
```

The executable test suite needs no external test packages or live PostgreSQL. It exercises argument safety, backup modes/fallback, restore selection, safeguards, version refusal, verification diagnostics, recovery state, cancellation and timeouts using a fake engine plus real harmless subprocesses. It does not substitute for a live backup/recovery drill. Use [the live acceptance procedure](examples/LIVE_VALIDATION.md) in a disposable compatible container before production use.

References: [pgBackRest commands](https://pgbackrest.org/command.html), [configuration/retention](https://pgbackrest.org/configuration.html), [PostgreSQL continuous archiving](https://www.postgresql.org/docs/18/continuous-archiving.html).

## Archive inspection

`InspectArchiveAsync()` lists the stanza archive and retrieves each listed WAL segment and timeline history file through pgBackRest, recording readable files, history contents and gaps between observed segment names on each timeline. It persists a report in the state directory. The operation reads the entire listed archive and may be expensive; it enumerates the local repository incrementally and retrieves names in configurable batches. Multiple archive generations are refused. The default inventory budget is 1,000,000 filesystem entries, with batches of 256 names and a total history-content budget of 16 MiB. Budgets are configured through ArchiveInspectionEntryLimit, ArchiveInspectionBatchSize and ArchiveInspectionHistoryBytes. Exceeding the inventory budget fails the inspection before retrieval and gap analysis. History content over budget is reported as unreadable. Each retrieved file is deleted before the next retrieval, and inspection requires headroom for one 1 GiB WAL segment above MinimumFreeBytes. Retrieval errors are reported as unreadable, not necessarily missing. Gaps before the first or after the last listed segment cannot be inferred. The report does not interpret timeline branches, validate WAL records, establish target reachability, or replace a recovery drill. The archive can change during or after inspection.
