using System.Globalization;
using System.Text.Json;
using BackupService;
using BackupServiceNew.PgBackRestWrapper;

namespace BackupServiceNew.Recovery;

internal sealed class RecoveryExecution(
    WrapperOptions options, BackupServiceOptions settings, PgBackRestClient client, ICommandRunner runner,
    RecoveryReadiness readiness, RecoveryPlans plans)
{
    private static string ScratchPath => Path.GetTempPath();

    public long ScratchFreeBytes => new DriveInfo(RecoveryReadiness.FindMount(ScratchPath) ?? "/").AvailableFreeSpace;

    public async Task<RestoreReceipt> RestoreAsync(RecoveryPlan plan, string? rollback, IProgress<OutputLine> progress,
        Func<Task> onRestore, CancellationToken ct)
    {
        // The wrapper invokes this guard while holding its cross-process operation lease, immediately
        // before starting pgBackRest. The backup worker also serializes service maintenance.
        var guarded = new PgBackRestClient(options, new RestoreGuard(runner, async token =>
        {
            plans.Revalidate(plan, await readiness.CheckAsync(token));
            if (plan.Populated) plans.ValidateRollback(plan, rollback);
            await onRestore();
        }));
        return await guarded.RestoreAsync(Request(plan, plan.Destination, plan.Populated, rollback), progress, ct);
    }

    public async Task<RestoreReceipt> RehearseAsync(RecoveryPlan plan, IProgress<OutputLine> progress, CancellationToken ct)
    {
        if (plan.Mode == RecoveryMode.Latest)
            throw new InvalidOperationException("Choose a fixed recovery target for a rehearsal.");
        if (ScratchFreeBytes < plan.RequiredBytes)
            throw new IOException($"Scratch recovery needs {RecoveryConsole.Bytes(plan.RequiredBytes)} free on {ScratchPath}.");

        // Keep the parent path short for Unix-domain sockets and private from other users.
        var directory = Directory.CreateTempSubdirectory("fs-rehearse-").FullName;
        var data = Path.Combine(directory, "data");
        var socket = Path.Combine(directory, "sock");
        var log = Path.Combine(directory, "postgres.log");
        var config = Path.Combine(directory, "postgresql.conf");
        var startAttempted = false;
        try
        {
            foreach (var path in new[] { options.DataPath, options.StatePath, options.RepositoryPath })
                Paths.Disjoint(directory, path);
            if (directory.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '-' or '_')))
                throw new IOException("The scratch path contains characters unsupported by pg_ctl.");
            var receipt = await client.RestoreAsync(Request(plan, data, false, null), progress, ct);
            Directory.CreateDirectory(socket);
            // Replace all restored settings. Only the recovery command and plan-controlled target
            // are enabled. Scratch PostgreSQL cannot listen on TCP or push WAL to the repository.
            var archiveGet = string.Join(' ', new[] { options.PgBackRest, $"--config={options.ConfigPath}",
                $"--stanza={options.Stanza}", $"--pg1-path={data}", "--repo=1", "--no-archive-async",
                "--log-level-console=off", "archive-get" }.Select(ShellQuote)) + " \"%f\" \"%p\"";
            var auto = Path.Combine(data, "postgresql.auto.conf");
            var target = plan.Target is { } time
                ? $"recovery_target_time = '{time.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}+00'\nrecovery_target_action = 'pause'\nrecovery_target_timeline = 'latest'\n"
                : "recovery_target = 'immediate'\nrecovery_target_action = 'promote'\n";
            await File.WriteAllTextAsync(auto, $"restore_command = {ConfigQuote(archiveGet)}\n{target}", ct);
            File.Delete(Path.Combine(data, "standby.signal"));
            await File.WriteAllTextAsync(Path.Combine(data, "recovery.signal"), "", ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "pg_hba.conf"), "local all all trust\n", ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "pg_ident.conf"), "", ct);
            await File.WriteAllTextAsync(config, $"""
                data_directory = {ConfigQuote(data)}
                listen_addresses = ''
                unix_socket_directories = {ConfigQuote(socket)}
                unix_socket_permissions = 0700
                port = 5432
                archive_mode = off
                archive_command = ''
                ssl = off
                hot_standby = on
                shared_preload_libraries = ''
                hba_file = {ConfigQuote(Path.Combine(directory, "pg_hba.conf"))}
                ident_file = {ConfigQuote(Path.Combine(directory, "pg_ident.conf"))}
                """, ct);

            startAttempted = true;
            await Run(options.PgCtl, ["-D", data, "-l", log, "-w", "-t",
                Math.Ceiling(options.RecoveryTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                "-o", $"-c config_file={config}", "start"], options.RecoveryTimeout + TimeSpan.FromSeconds(10), ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.RecoveryTimeout);
            var assertions = settings.ExpectedDatabases.Length == 0
                ? [new SqlAssertion("postgres", "SELECT 1;", "1")]
                : settings.ExpectedDatabases.Select(database => new SqlAssertion(database, UserTableAssertion, "t")).ToArray();
            while (true)
            {
                var observed = await client.ObserveRecoveryAsync(receipt.Id, socket, 5432, assertions, deadline.Token);
                if (observed.Stage == RecoveryStage.Validated) return observed;
                await Task.Delay(500, deadline.Token);
            }
        }
        finally
        {
            if (File.Exists(log))
            {
                using var stream = File.OpenRead(log);
                stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                progress.Report(new(DateTimeOffset.UtcNow, false, await reader.ReadToEndAsync()));
            }
            if (startAttempted && File.Exists(Path.Combine(data, "postmaster.pid")))
                await Run(options.PgCtl, ["-D", data, "-m", "immediate", "-w", "stop"],
                    TimeSpan.FromSeconds(60), CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    public async Task<RecoveryObservation> ObserveAsync(RecoveryPlan plan, Guid receiptId, bool previouslyPaused, CancellationToken ct)
    {
        var state = await Query("postgres",
            "SELECT json_build_object('directory', current_setting('data_directory'), 'recovering', pg_is_in_recovery());", ct);
        using var json = JsonDocument.Parse(state);
        if (Paths.Absolute(json.RootElement.GetProperty("directory").GetString()!) != plan.Destination)
            throw new InvalidOperationException("The PostgreSQL socket is serving a different data directory.");

        if (json.RootElement.GetProperty("recovering").GetBoolean())
        {
            if (plan.Mode == RecoveryMode.Timestamp)
            {
                var receipt = await client.ObserveRecoveryAsync(receiptId, options.SocketPath, options.Port, cancellationToken: ct);
                if (receipt.Stage == RecoveryStage.TargetReached)
                    return new("target-paused", "PostgreSQL is paused at the configured target. Inspect the data before accepting recovery.");
                if (previouslyPaused)
                    return new("target-paused", "The target was observed paused earlier. Waiting for PostgreSQL to leave recovery.");
            }
            return new("replaying", "PostgreSQL is running and WAL recovery is still in progress.");
        }

        if (plan.Mode == RecoveryMode.Timestamp && !previouslyPaused)
            throw new InvalidOperationException("PostgreSQL has left recovery, but this console did not observe the requested target paused.");
        foreach (var database in settings.ExpectedDatabases)
            if (await Query(database, UserTableAssertion, ct) != "t")
                throw new InvalidOperationException($"Expected PostgreSQL database {database} has no user tables.");

        return new("database-validated", settings.ExpectedDatabases.Length == 0
            ? "PostgreSQL has left recovery. PostgreSQL responded to the SQL probe. Application checks remain outstanding."
            : $"PostgreSQL has left recovery. User tables are accessible in {string.Join(", ", settings.ExpectedDatabases)}. Application checks remain outstanding.");
    }

    private const string UserTableAssertion =
        "SELECT EXISTS (SELECT FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%');";

    private async Task<string> Query(string database, string sql, CancellationToken ct) =>
        (await Run(options.Psql, ["-X", "-w", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-h", options.SocketPath,
                "-p", options.Port.ToString(CultureInfo.InvariantCulture), "-U", options.DatabaseUser, "-d", database, "-c", sql],
            TimeSpan.FromSeconds(10), ct)).Output.Trim();

    private async Task<CommandResult> Run(string executable, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var result = await runner.RunAsync(new(executable, args, timeout), cancellationToken: ct);
        if (!result.Success) throw new CommandFailedException(result);
        return result;
    }

    private RestoreRequest Request(RecoveryPlan plan, string destination, bool delta, string? rollback) => new()
    {
        Destination = destination, BackupLabel = plan.Backup.Label, Mode = plan.Mode, Timestamp = plan.Target,
        ConfirmStanza = options.Stanza, ConfirmServerStopped = true, Delta = delta, PreservedRollbackDirectory = rollback,
        MinimumFreeBytes = plan.RequiredBytes
    };

    private static string ConfigQuote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private sealed class RestoreGuard(ICommandRunner inner, Func<CancellationToken, Task> validate) : ICommandRunner
    {
        public async Task<CommandResult> RunAsync(CommandSpec command, IProgress<OutputLine>? progress = null, CancellationToken cancellationToken = default)
        {
            if (command.Arguments.LastOrDefault() == "restore" && command.Arguments.Any(arg => arg.StartsWith("--pg1-path=", StringComparison.Ordinal)))
            {
                await validate(cancellationToken);
                progress?.Report(new(DateTimeOffset.UtcNow, false, "Readiness and the pinned source passed rechecks. Restoring database files."));
            }
            return await inner.RunAsync(command, progress, cancellationToken);
        }
    }
}

internal sealed record RecoveryObservation(string Stage, string Detail);
