using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BackupService;
using BackupService.PgBackRest;
using BackupServiceNew.PgBackRestWrapper;
using Shared.Backups;
using Shared.Messaging;

namespace BackupServiceNew;

internal sealed class BackupJobs(
    WrapperOptions options,
    BackupServiceOptions settings,
    PgBackRestClient client,
    BackupRepository repository,
    OpenBaoPairing openBao,
    IBackgroundRunReporter runReporter,
    ILogger<BackupJobs> logger) : BackgroundService
{
    private readonly string jobsPath = Path.Combine(options.StatePath, "jobs");
    private readonly ReportStore jobFiles = new(Path.Combine(options.StatePath, "jobs"));
    private readonly ConcurrentDictionary<Guid, Job> jobs = new();
    private readonly ConcurrentDictionary<Guid, JobProgress> liveProgress = new();
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim admission = new(1, 1);
    private FileStream? workerLease;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Paths.Writable(jobsPath);
        workerLease = new FileStream(Path.Combine(jobsPath, "worker.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        foreach (var path in Directory.EnumerateFiles(jobsPath, "*.json"))
        {
            Job? job;
            try
            {
                job = await jobFiles.ReadAsync<Job>(Path.GetFileName(path));
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Could not read backup job file {Path}.", path);
                continue;
            }

            if (job is null)
                continue;

            if (job.Status == "running")
            {
                // The wrapper assigns its operation annotation internally. An interrupted command
                // may have committed a backup, so retain the idempotency key and never replay it.
                await SaveAsync(job with
                {
                    Status = "failed",
                    Error = "The backup service restarted before this job finished. Check the repository for any completed backup.",
                    CompletedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                jobs[job.Id] = job;
            }
        }

        foreach (var job in jobs.Values.Where(job => job.Status == "queued").OrderBy(job => job.CreatedAt))
            queue.Writer.TryWrite(job.Id);

        await base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        workerLease?.Dispose();
        base.Dispose();
    }

    public BackupJobDto[] List() => jobs.Values.OrderByDescending(job => job.CreatedAt).Select(Describe).ToArray();

    public BackupJobDto? Get(Guid id) => jobs.TryGetValue(id, out var job) ? Describe(job) : null;

    public async Task<BackupJobDto> QueueBackupAsync(CreateBackupJobRequest request, CancellationToken ct)
    {
        var type = BackupTypes.Normalize(request.Type);
        var now = DateTimeOffset.UtcNow;
        var name = BackupTypes.ValidateName(request.Scheduled && string.IsNullOrWhiteSpace(request.Name)
            ? $"scheduled-{type}-{now:yyyyMMdd-HHmmss'Z'}" : request.Name);
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey;

        await admission.WaitAsync(ct);
        try
        {
            if (key != null && jobs.Values.FirstOrDefault(job => job.IdempotencyKey == key) is { } existing)
            {
                if (existing.Type != type || (existing.Name != name && !string.IsNullOrWhiteSpace(request.Name))
                    || existing.Scheduled != request.Scheduled || existing.ScheduleKey != request.ScheduleKey)
                {
                    throw new ArgumentException("This idempotency key is already used by a different backup request.");
                }

                return Describe(existing);
            }

            return await EnqueueAsync(new Job(Guid.NewGuid(), "backup", now)
            {
                Type = type,
                Name = name,
                Scheduled = request.Scheduled,
                ScheduleKey = request.ScheduleKey,
                IdempotencyKey = key
            });
        }
        finally
        {
            admission.Release();
        }
    }

    public Task<BackupJobDto> QueueVerifyAsync(VerifyBackupRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return EnqueueAsync(new Job(Guid.NewGuid(), request.Deep ? "verify-deep" : "verify-quick", DateTimeOffset.UtcNow)
        {
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim()
        });
    }

    public Task<BackupJobDto> QueueMaintenanceAsync(string operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation is not ("check" or "expire" or "inspect-archive")) throw new ArgumentException("Unknown maintenance operation.");
        return EnqueueAsync(new Job(Guid.NewGuid(), operation, DateTimeOffset.UtcNow));
    }

    private async Task<BackupJobDto> EnqueueAsync(Job job)
    {
        await SaveAsync(job);
        queue.Writer.TryWrite(job.Id);
        return Describe(job);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            stoppingToken.ThrowIfCancellationRequested();
            await RunAsync(jobs[id], stoppingToken);
        }
    }

    private async Task RunAsync(Job job, CancellationToken ct)
    {
        await SaveAsync(job with { Status = "running", StartedAt = DateTimeOffset.UtcNow });
        var progress = new JobProgress();
        liveProgress[job.Id] = progress;
        try
        {
            if (job.Kind == "backup")
                await RunBackupAsync(jobs[job.Id], progress, ct);
            else if (job.Kind is "verify-quick" or "verify-deep")
                await RunVerifyAsync(job, progress, ct);
            else if (job.Kind is "check" or "expire" or "inspect-archive")
                await RunMaintenanceAsync(job.Kind, progress, ct);
            else throw new InvalidOperationException($"Unknown job kind '{job.Kind}'.");

            await FinishAsync("completed", null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup service job {JobId} failed.", job.Id);
            var message = ct.IsCancellationRequested
                ? "The backup service stopped during this job. Check the repository for any completed backup."
                : ex.Message[..Math.Min(ex.Message.Length, 4096)];
            await FinishAsync("failed", message);
        }
        finally
        {
            liveProgress.TryRemove(job.Id, out _);
        }

        Task FinishAsync(string status, string? error) => SaveAsync(jobs[job.Id] with
        {
            Status = status,
            Error = error,
            CompletedAt = DateTimeOffset.UtcNow,
            ResultSummary = progress.ResultSummary,
            Progress = progress.Lines()
        });
    }

    private async Task RunBackupAsync(Job job, JobProgress progress, CancellationToken ct)
    {
        var taskType = $"backup-{job.Type}";
        var key = job.IdempotencyKey ?? job.Id.ToString("N");
        await using var run = job.Scheduled
            ? await runReporter.BeginScheduledAsync(taskType, job.ScheduleKey ?? taskType, key, job.Name, ct)
            : await runReporter.BeginManualAsync(taskType, key, job.Name, ct);

        try
        {
            await repository.PrepareBackupAsync(job.Type!, progress.Add, ct);
            var type = job.Type switch
            {
                BackupTypes.Full => BackupType.Full,
                BackupTypes.Differential => BackupType.Differential,
                BackupTypes.Incremental => BackupType.Incremental,
                _ => throw new InvalidOperationException($"Unknown backup type '{job.Type}'.")
            };
            var result = await client.BackupAsync(type, job.Name, progress, ct);
            var backup = repository.Describe(result.Produced);
            job = job with { Label = backup.Label, Backup = backup };
            await SaveAsync(job);

            const string exportStep = "Saving the OpenBao Raft snapshot and secrets export.";
            progress.Add(exportStep);
            await run.ReportAsync(exportStep);
            try
            {
                await openBao.ExportForBackupAsync(backup.Label, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"PostgreSQL backup {backup.Label} was created, but the OpenBao export failed: {ex.Message}", ex);
            }

            await SaveAsync(job with { Backup = repository.Describe(result.Produced) });
            if (backup.Type != job.Type)
                throw new InvalidOperationException($"pgBackRest created a {backup.Type} backup ({backup.Label}) after a {job.Type} backup was requested.");

            run.Succeed($"Backup {backup.Label} completed.");
        }
        catch (Exception ex)
        {
            run.Fail(ex.Message);
            throw;
        }
    }

    private async Task RunMaintenanceAsync(string kind, JobProgress progress, CancellationToken ct)
    {
        if (kind == "inspect-archive")
        {
            progress.Add("Inspecting archived WAL. This may take a long time.");
            var inspection = await client.InspectArchiveAsync(ct);
            var unreadable = inspection.Segments.Where(segment => !segment.Readable).Select(segment => $"{segment.Name}: {segment.Error}")
                .Concat(inspection.Histories.Where(history => !history.Readable).Select(history => $"{history.Name}: {history.Error}"))
                .ToArray();
            progress.ResultSummary = $"Inspected {inspection.Segments.Count} WAL segments and {inspection.Histories.Count} history files. Found {inspection.Gaps.Count} gaps within the observed range and {unreadable.Length} unreadable files. Recovery target reachability has not been assessed.";
            progress.Add($"Inspected {inspection.Segments.Count} WAL segments and {inspection.Histories.Count} history files. Gaps: {inspection.Gaps.Count}. Unreadable files: {unreadable.Length}.");
            foreach (var gap in inspection.Gaps.Take(30))
                progress.Add($"Gap on timeline {gap.Timeline}: {gap.FirstMissing} through {gap.LastMissing}.");
            foreach (var item in unreadable.Take(30))
                progress.Add($"Unreadable: {item}");
            foreach (var warning in inspection.Warnings.Take(30))
                progress.Add($"Warning: {warning}");
            if (inspection.Gaps.Count > 30 || unreadable.Length > 30 || inspection.Warnings.Count > 30)
                progress.Add("Job output is limited to the first 30 entries of each category. The complete report is saved in the backup service state directory.");
            return;
        }
        if (kind == "check")
        {
            progress.Add("Checking pgBackRest configuration and WAL archiving.");
            var result = await client.CheckAsync(ct);
            progress.Add(result.Output);
            progress.ResultSummary = "Configuration and current WAL archiving passed the pgBackRest check.";
            return;
        }
        progress.Add("Expiring backups according to configured retention.");
        var before = await client.GetRepositoryAsync(ct);
        if (before.StatusCode != 0) throw new InvalidOperationException(before.StatusMessage);
        var expired = await client.ExpireAsync(ct);
        progress.Add(expired.Output);
        var after = await client.GetRepositoryAsync(ct);
        if (after.StatusCode != 0) throw new InvalidOperationException(after.StatusMessage);
        var removed = before.Backups.Select(b => b.Label).Except(after.Backups.Select(b => b.Label)).ToArray();
        progress.Add(removed.Length == 0 ? "No backup labels expired." : $"Expired labels: {string.Join(", ", removed)}");
        progress.Add("OpenBao companion files are retained and are not removed by expiration.");
    }

    private async Task RunVerifyAsync(Job job, JobProgress progress, CancellationToken ct)
    {
        await client.VerifyAsync(job.Label, progress, ct);
        progress.ResultSummary = job.Label is null
            ? "Repository checksum verification passed."
            : $"Checksum verification passed for backup {job.Label}.";
        if (job.Kind == "verify-deep")
        {
            var scratch = new ScratchRequest(Path.Combine(Path.GetTempPath(), "froststream-verify"),
                job.Label, settings.ExpectedDatabases);
            var result = await client.TestRestoreAsync(scratch, progress, ct);
            progress.ResultSummary = "Checksum verification and temporary restore validation passed.";
            if (!result.CleanedUp)
                throw new InvalidOperationException($"Scratch restore cleanup failed for {result.Directory}.");
        }
    }

    private async Task SaveAsync(Job job)
    {
        await jobFiles.WriteAsync($"{job.Id:N}.json", job);
        jobs[job.Id] = job;
    }

    private BackupJobDto Describe(Job job) => new(job.Id, job.Kind, job.Type, job.Status, job.Name, job.Label,
        job.Error, job.CreatedAt, job.CompletedAt,
        job.Status == "running" && liveProgress.TryGetValue(job.Id, out var progress) ? progress.Lines() : job.Progress)
    {
        ResultSummary = job.ResultSummary,
        ActualType = job.Backup?.Type,
        Backup = job.Backup,
        StartedAt = job.StartedAt
    };

    private sealed record Job(Guid Id, string Kind, DateTimeOffset CreatedAt)
    {
        public string? ResultSummary { get; init; }
        public string Status { get; init; } = "queued";
        public string? Name { get; init; }
        public string? Type { get; init; }
        public string? Label { get; init; }
        public BackupInfoDto? Backup { get; init; }
        public string? Error { get; init; }
        public string? ScheduleKey { get; init; }
        public string? IdempotencyKey { get; init; }
        public bool Scheduled { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string[] Progress { get; init; } = [];
    }

    private sealed class JobProgress : IProgress<OutputLine>
    {
        public string? ResultSummary { get; set; }

        private readonly Queue<string> lines = new();

        public void Report(OutputLine value) => Add(value.Text.TrimEnd());

        public void Add(string text)
        {
            lock (lines)
            {
                lines.Enqueue(text);
                if (lines.Count > 100)
                    lines.Dequeue();
            }
        }

        public string[] Lines()
        {
            lock (lines)
                return lines.ToArray();
        }
    }
}
