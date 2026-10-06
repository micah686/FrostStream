using System.Collections.Concurrent;
using System.Threading.Channels;
using BackupService.PgBackRest;
using Shared.Messaging;

namespace BackupService;

/// <summary>
/// Serialized work queue for every pgBackRest operation (backup, verify, deep verify, restore).
/// A single queue is deliberate: pgBackRest holds a process-level lock per stanza anyway, and a
/// restore must never interleave with anything else. Job state is durable via
/// <see cref="BackupJobStore"/>; live output lines are buffered in memory per job for the jobs
/// API and the restore wizard.
/// </summary>
internal sealed class BackupCoordinator(
    BackupJobStore store,
    PgBackRestRunner runner,
    DeepVerifyRunner deepVerify,
    OpenBaoPairing openBaoPairing,
    PostgresStateProbe stateProbe,
    BackupServiceOptions options,
    IBackgroundRunReporter runReporter,
    ILogger<BackupCoordinator> logger) : BackgroundService
{
    private const int MaxProgressLines = 400;

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<BackupJobRecord>> _completion = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _progress = new();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public async Task<BackupJobRecord> QueueBackupAsync(
        string? requestedName,
        string? requestedType,
        bool scheduled,
        string? scheduleKey,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey)
            && store.FindByIdempotencyKey(idempotencyKey) is { Status: not "failed" } existing)
            return existing;

        var type = string.IsNullOrWhiteSpace(requestedType)
            ? PgBackRestRunner.TypeFull
            : requestedType.Trim().ToLowerInvariant();
        if (type is not (PgBackRestRunner.TypeFull or PgBackRestRunner.TypeDiff))
            throw new ArgumentException($"Unsupported backup type '{requestedType}'.", nameof(requestedType));

        var name = SanitizeName(string.IsNullOrWhiteSpace(requestedName)
            ? $"froststream-{type}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : requestedName);
        return await EnqueueAsync(new BackupJobRecord
        {
            JobId = Guid.NewGuid(),
            Status = "queued",
            Kind = BackupJobKinds.Backup,
            Name = name,
            Type = type,
            Scheduled = scheduled,
            ScheduleKey = scheduleKey,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    /// <summary>
    /// Quick verify checks the whole repository (pgbackrest verify has no per-backup scope);
    /// deep verify test-restores one backup — <paramref name="label"/> null means the latest.
    /// </summary>
    public Task<BackupJobRecord> QueueVerifyAsync(string? label, bool deep, CancellationToken cancellationToken)
        => EnqueueAsync(new BackupJobRecord
        {
            JobId = Guid.NewGuid(),
            Status = "queued",
            Kind = deep ? BackupJobKinds.VerifyDeep : BackupJobKinds.VerifyQuick,
            Label = deep ? label : null,
            Scheduled = false,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

    public Task<BackupJobRecord> QueueRestoreAsync(
        string? label,
        DateTimeOffset? targetTime,
        CancellationToken cancellationToken)
        => EnqueueAsync(new BackupJobRecord
        {
            JobId = Guid.NewGuid(),
            Status = "queued",
            Kind = BackupJobKinds.Restore,
            Label = label,
            TargetTime = targetTime,
            Scheduled = false,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

    public async Task<BackupJobRecord> WaitAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (_completion.TryGetValue(jobId, out var pending))
            return await pending.Task.WaitAsync(cancellationToken);
        return store.Get(jobId) ?? throw new KeyNotFoundException($"Backup job {jobId} was not found.");
    }

    public IReadOnlyList<string> GetProgress(Guid jobId)
        => _progress.TryGetValue(jobId, out var lines) ? lines.ToArray() : [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (store.Get(jobId) is { } record)
                await RunAsync(record, stoppingToken);
        }
    }

    private async Task<BackupJobRecord> EnqueueAsync(BackupJobRecord record, CancellationToken cancellationToken)
    {
        await store.SaveAsync(record, cancellationToken);
        _completion[record.JobId] = new TaskCompletionSource<BackupJobRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(record.JobId, cancellationToken);
        return record;
    }

    private async Task RunAsync(BackupJobRecord record, CancellationToken cancellationToken)
    {
        record = record with { Status = "running" };
        await store.SaveAsync(record, cancellationToken);
        var progress = ProgressFor(record.JobId);

        try
        {
            record = record.Kind switch
            {
                BackupJobKinds.Backup => await RunBackupAsync(record, progress, cancellationToken),
                BackupJobKinds.VerifyQuick => await RunVerifyAsync(record, progress, cancellationToken),
                BackupJobKinds.VerifyDeep => await RunDeepVerifyAsync(record, progress, cancellationToken),
                BackupJobKinds.Restore => await RunRestoreAsync(record, progress, cancellationToken),
                _ => throw new InvalidOperationException($"Unknown job kind '{record.Kind}'.")
            };
            record = record with { Status = "completed", CompletedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(record, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "{Kind} job {JobId} failed.", record.Kind, record.JobId);
            progress.Report($"Failed: {ex.Message}");
            record = record with
            {
                Status = "failed",
                ErrorMessage = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(record, CancellationToken.None);
        }
        finally
        {
            if (_completion.TryRemove(record.JobId, out var completion))
                completion.TrySetResult(record);
        }
    }

    private async Task<BackupJobRecord> RunBackupAsync(
        BackupJobRecord record,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        // Reported here rather than by the caller because this is where both paths converge: an
        // admin-triggered backup arrives over HTTP and never touches the Scheduler. The reporter
        // swallows publish failures, so a down NATS never blocks the backup itself.
        var taskType = record.Type == PgBackRestRunner.TypeDiff ? "backup-diff" : "backup-full";
        var idempotencyKey = record.IdempotencyKey ?? record.JobId.ToString("N");
        var detail = $"{record.Type} · {record.Name}";
        await using var run = record.Scheduled
            ? await runReporter.BeginScheduledAsync(
                taskType, record.ScheduleKey ?? taskType, idempotencyKey, detail, cancellationToken)
            : await runReporter.BeginManualAsync(taskType, idempotencyKey, detail, cancellationToken);

        try
        {
            await run.ReportAsync($"Running pgBackRest {record.Type} backup '{record.Name}'…");
            var label = await runner.BackupAsync(record.Type!, record.Name!, progress, cancellationToken);
            progress.Report($"Backup complete: {label}.");

            await run.ReportAsync("Backing up OpenBao (storage snapshot + secrets export)…");
            progress.Report("Backing up OpenBao (storage snapshot + secrets export)…");
            await openBaoPairing.ExportForBackupAsync(label, cancellationToken);

            // pgBackRest expired old backups after this one; drop their paired exports too.
            var live = (await runner.InfoAsync(cancellationToken))?.Backup
                .Select(b => b.Label)
                .OfType<string>()
                .ToArray() ?? [];
            openBaoPairing.PruneExcept(live);

            run.Succeed($"Backup {label} completed.");
            return record with { Label = label };
        }
        catch (Exception ex)
        {
            run.Fail(ex.Message);
            throw;
        }
    }

    private async Task<BackupJobRecord> RunVerifyAsync(
        BackupJobRecord record,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        progress.Report("Verifying repository integrity (pgbackrest verify)…");
        await runner.VerifyAsync(progress, cancellationToken);
        progress.Report("Repository verification passed.");
        return record;
    }

    private async Task<BackupJobRecord> RunDeepVerifyAsync(
        BackupJobRecord record,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        await deepVerify.RunAsync(record.Label, progress, cancellationToken);
        return record;
    }

    private async Task<BackupJobRecord> RunRestoreAsync(
        BackupJobRecord record,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var state = await stateProbe.ProbeAsync(cancellationToken);
        if (state.ServerRunning)
            throw new InvalidOperationException(
                "PostgreSQL is still running; stop the postgres container before restoring.");

        progress.Report(record.TargetTime is { } target
            ? $"Restoring to point-in-time {target:yyyy-MM-dd HH:mm:ss} UTC…"
            : $"Restoring {(record.Label is null ? "the latest backup" : $"backup {record.Label}")}…");
        await runner.RestoreAsync(
            record.Label,
            record.TargetTime,
            options.PgDataPath,
            immediateTarget: false,
            progress,
            cancellationToken);
        progress.Report("Restore finished. Start the postgres container to begin recovery.");
        return record;
    }

    private IProgress<string> ProgressFor(Guid jobId)
    {
        var lines = _progress.GetOrAdd(jobId, _ => new ConcurrentQueue<string>());
        return new LineProgress(line =>
        {
            lines.Enqueue(line);
            while (lines.Count > MaxProgressLines)
                lines.TryDequeue(out _);
            logger.LogInformation("Job {JobId}: {Line}", jobId, line);
        });
    }

    private static string SanitizeName(string value)
    {
        var safe = new string(value.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
    }

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; would marshal via the sync context and reorder lines.</summary>
    private sealed class LineProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}
