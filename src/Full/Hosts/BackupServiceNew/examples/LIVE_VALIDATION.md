# Live wrapper acceptance

For a complete containerized fixture, run `docker compose up --build --abort-on-container-exit --exit-code-from tests` from the repository root. See [the Compose test guide](../docker/README.md). The instructions below are for running directly on a host with the tools installed.

Use a disposable environment with PostgreSQL 14+ matching-major `initdb`, `postgres`, `pg_ctl`, `psql`, `pg_dump`, plus pgBackRest supporting `verify`, zstd and the selected storage optimizations. Run as an unprivileged PostgreSQL-compatible OS user. Physical restore requires compatible platform, extensions and image libraries. The .NET 11 SDK is needed to run the harness from source.

```sh
dotnet run --project PgBackRestWrapper.Tests -- --live
```

The harness creates a private cluster, Unix sockets and repository beneath a unique temporary directory. It does not read a production PGDATA environment variable. It tests full/diff/incr consistency (one/two/three identifiable rows), repository verification, timestamp PITR (three rows) and latest archived state (four rows). TCP is disabled. It stops only servers it starts, retains its directories/reports, and prints their location. On failure review the retained logs and check for any failed shutdown before deleting anything.

Complete these additional checks in that disposable repository before accepting a specific image:

1. Confirm the actual produced backup types are full, diff and incr; inspect `compatibility.json`, command reports and backup metadata. Repeat with bundles/blocks enabled. Verify and restore compressed WAL and backup files.
2. Preserve an independent copy of a stopped restored cluster. Restore an older set into that populated destination with `Delta=true` and the rollback path. Verify expected rows and removal of later data; compare against the same set restored into an empty destination.
3. Copy the test repository before introducing a corrupt backup file, missing chain dependency or missing required WAL. Require verify/recovery to fail. A successful file-copy restore must never become `Validated` without subsequent PostgreSQL recovery and SQL assertions. For an unreached timestamp require pending/failure, and inspect PostgreSQL's recovery logs.
4. Reduce retention in a separate disposable configuration, expire, inspect backup references/WAL endpoints and restore all retained sets. Check a retained timestamp target as well. Counts do not establish days or continuous WAL coverage.
5. Test named restore point and LSN recovery, exact target settings, selected timeline/history and exclusive targets. Use a selected set known to precede a named point. Inspect logs to distinguish automatic target pause from any operator pause; do not manually pause while the observer is collecting evidence.
6. Test cancellation, a read-only/full destination, stale PID, running PostgreSQL, incompatible major tools and simultaneous mutating clients. Confirm no false success and preserve diagnostics.
7. Stop live PostgreSQL during an operation and restart only after inspecting state. Missing final reports are interrupted/unknown; there is no worker to reconcile them.

Use representative application SQL assertions, not only row counts. Protect report directories and preserve needed history. After a real recovery, restore intended PostgreSQL configuration, check archiving again, and take a fresh full backup. Configuration outside PGDATA, roles for logical export, extensions, off-host copies and recovery keys require separate preservation.

## Recovery console workflow

The console follows `recovery-console-demo.html`: recovery type, readiness, source selection,
confirmation, database recovery, OpenBao, additional data selection, and additional data recovery.
All status and inventory come from the service; the demo's simulated success states are not used.

- `BACKUP_RESTORE_UI_TOKEN` takes precedence over `Backup:RestoreUiToken`. Blank values fall
  through to the alias; when both are blank, a fresh 32-byte random token (hex encoded) is logged
  once in the `FROSTSTREAM RESTORE CONSOLE TOKEN` block. AppHost passes blank configuration
  through without a development credential. Restarting invalidates generated credentials and cookies.
- Physical recovery requires stopped PostgreSQL and writers. For populated PGDATA, confirmation
  requires the path to an independently preserved copy on a separate mount. Optional rehearsal
  restores the pinned source and recovery target into a private temporary cluster, runs SQL checks,
  stops the cluster, and cleans up. It requires additional temporary storage.
- Logical recovery requires running PostgreSQL at the configured data directory and stopped writers.
  It verifies the entire bundle and checks every dump with `pg_restore --list` before dropping any
  database. Every database recorded in the manifest is recreated and restored. Role ownership and
  privileges remain deployment-managed. Interrupted jobs are failed on restart, never replayed.
- OpenBao uses the companion snapshot. Additional-data recovery supports the OpenBao and ASP.NET
  Data Protection keys actually included in a bundle, verifies hashes, and overwrites matching files
  at configured destinations. AppHost mounts those key destinations writable for recovery. Preserve
  independent key copies first; other deployment configurations must provide writable mounts too.
- Arbitrary application configuration and media are outside the current bundle format. The console
  identifies these as separate operator recovery work, rather than claiming they were restored.

Automated checks: build `BackupServiceNew`, then build/run `src/Tests/BackupBundleTests` for token,
logical restore preflight, corruption rejection and key restore checks. Before production use,
exercise physical recovery, exact-time rehearsal, logical replacement and OpenBao recovery against
throwaway PostgreSQL/OpenBao instances with representative backups and verify application access.
