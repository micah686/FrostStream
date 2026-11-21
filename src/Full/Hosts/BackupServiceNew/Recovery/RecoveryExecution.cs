using System.Globalization;
using System.Text.Json;
using BackupService;
using BackupServiceNew.PgBackRestWrapper;

namespace BackupServiceNew.Recovery;

internal sealed class RecoveryExecution(
    WrapperOptions options, BackupServiceOptions settings, PgBackRestClient client, ICommandRunner runner,
    RecoveryReadiness readiness, RecoveryPlans plans)
{
    public async Task<RestoreReceipt> RestoreAsync(RecoveryPlan plan, string? rollback, IProgress<OutputLine> progress,
        Func<Task> onRestore, CancellationToken ct)
    {
        // The wrapper invokes this guard while holding its cross-process operation lease, immediately
        // before starting pgBackRest. The backup worker also serializes service maintenance.
        var guarded = new PgBackRestClient(options, new RestoreGuard(runner, async token =>
        {
            plans.Revalidate(plan, await readiness.CheckAsync(token));
            if (plan.Populated && !string.IsNullOrWhiteSpace(rollback)) plans.ValidateRollback(plan, rollback);
            await onRestore();
        }));
        return await guarded.RestoreAsync(Request(plan, plan.Destination, plan.Populated, rollback), progress, ct);
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
