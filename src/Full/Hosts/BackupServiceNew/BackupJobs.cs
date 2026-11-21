using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BackupService;
using BackupService.PgBackRest;
using BackupServiceNew.PgBackRestWrapper;
using BackupServiceNew.Recovery;
using Shared.Backups;
using Shared.Messaging;

namespace BackupServiceNew;

internal sealed class BackupJobs(
    WrapperOptions options,
    BackupServiceOptions settings,
    PgBackRestClient client,
    BackupRepository repository,
    LogicalBackups logical,
    OpenBaoPairing openBao,
    RecoveryExecution recovery,
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

            if (job.Status == "running" || (IsRecovery(job)) && job.Status == "queued")
            {
                // The wrapper assigns its operation annotation internally. An interrupted command
                // may have committed a backup, so retain the idempotency key and never replay it.
                await SaveAsync(job with
                {
                    Status = "failed",
                    Error = IsRecovery(job)
                        ? "The service restarted before this operation finished. Review its logs and inspect the destination before starting another restore."
                        : "The backup service restarted before this job finished. Check the repository for any completed backup.",
                    Progress = IsRecovery(job) ? ReadRecoveryLog(job.Id) : job.Progress,
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

    public BackupJobDto[] List() => jobs.Values.Where(job => !IsRecovery(job))
        .OrderByDescending(job => job.CreatedAt).Select(Describe).ToArray();

    public BackupJobDto? Get(Guid id) => jobs.TryGetValue(id, out var job) && !IsRecovery(job) ? Describe(job) : null;

    public RecoveryJob? GetRecovery(Guid id) => jobs.TryGetValue(id, out var job) && IsRecovery(job) ? DescribeRecovery(job) : null;

    public RecoveryJob? GetPlanJob(Guid planId, string kind) => jobs.Values
        .Where(job => job.Kind == kind && job.Plan?.Id == planId)
        .OrderByDescending(job => job.CreatedAt).Select(DescribeRecovery).FirstOrDefault();

    public RecoveryJob[] RecentRecoveries() => jobs.Values.Where(job => job.Kind == "restore")
        .OrderByDescending(job => job.CreatedAt).Take(5).Select(DescribeRecovery).ToArray();

    public async Task<Guid> QueueRecoveryAsync(RecoveryPlan plan, string kind, string? rollback, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            var existing = kind == "restore" ? GetRecovery(plan.Id) : GetPlanJob(plan.Id, kind);
            if (existing is not null) return existing.Id;
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("The recovery plan expired. Choose a recovery source again.");
            if (jobs.Values.Any(job => IsRecovery(job) && job.Status is "queued" or "running"))
                throw new InvalidOperationException("A recovery operation is already queued or running. Wait for it to finish.");
            if (kind == "restore" && (GetPlanJob(plan.Id, "archive")?.Stage == "archive-failed"
                || GetPlanJob(plan.Id, "checksum")?.Stage == "checksum-failed"))
                throw new InvalidOperationException("The pinned source failed a recovery check. Investigate the failure and create a new plan.");
            var id = kind == "restore" ? plan.Id : Guid.NewGuid();
            await EnqueueAsync(new Job(id, kind, DateTimeOffset.UtcNow)
            {
                Plan = plan, Rollback = rollback, Label = plan.Backup.Label, Stage = "queued"
            });
            return id;
        }
        finally
        {
            admission.Release();
        }
    }

    public async Task<Guid> QueueOtherAsync(Guid id, string[] items, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            if (GetRecovery(id) is not { Kind: "restore", Status: "completed", Stage: "database-validated" } database)
                throw new InvalidOperationException("Complete database recovery first.");
            if (jobs.Values.Any(job => job.Kind == "restore" && job.CreatedAt > database.CreatedAt))
                throw new InvalidOperationException("A newer restore exists.");
            if (GetPlanJob(id, "other") is { } existing) return existing.Id;
            if (jobs.Values.Any(job => IsRecovery(job) && job.Status is "queued" or "running"))
                throw new InvalidOperationException("Another recovery operation is running.");
            if (items.Length == 0 || items.Except(logical.OtherData(database.Plan.Backup.Label)).Any())
                throw new InvalidOperationException("Select available backup data.");
            var jobId = Guid.NewGuid();
            await EnqueueAsync(new Job(jobId, "other", DateTimeOffset.UtcNow) { Plan = database.Plan, Stage = "queued", OtherItems = items, CompanionHash = await logical.CompanionHashAsync(database.Plan.Backup.Label, ct) });
            return jobId;
        }
        finally { admission.Release(); }
    }

    public async Task QueueOpenBaoAsync(Guid id, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            if (!jobs.TryGetValue(id, out var job) || job is not { Kind: "restore", Status: "completed", Stage: "database-validated", Plan: { } plan })
                throw new InvalidOperationException("Complete the PostgreSQL recovery check first.");
            if (jobs.Values.Any(other => other.Kind == "restore" && other.CreatedAt > job.CreatedAt))
                throw new InvalidOperationException("A newer restore exists. Use its recovery page.");
            if (GetPlanJob(plan.Id, "openbao") is not null)
                throw new InvalidOperationException("An OpenBao restore was already started for this recovery. Inspect its result before taking further action.");
            if (jobs.Values.Any(other => IsRecovery(other) && other.Status is "queued" or "running"))
                throw new InvalidOperationException("Another recovery operation is running.");
            if (plan.Backup.Mode != "logical" && !openBao.HasSnapshotExport(plan.Backup.Label))
                throw new InvalidOperationException("The paired OpenBao snapshot is missing.");
            var observation = plan.Backup.Mode == "logical" ? null : await recovery.ObserveAsync(plan, job.ReceiptId ?? throw new InvalidOperationException("Missing restore receipt."), job.TargetWasPaused, ct);
            if (observation is not null && observation.Stage != "database-validated")
                throw new InvalidOperationException("PostgreSQL has not completed recovery. Run the recovery check again.");
            await EnqueueAsync(new Job(Guid.NewGuid(), "openbao", DateTimeOffset.UtcNow)
                { Plan = plan, Label = plan.Backup.Label, Stage = "queued" });
        }
        finally { admission.Release(); }
    }

    public async Task ObserveRecoveryAsync(Guid id, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            if (!jobs.TryGetValue(id, out var job) || job is not { Kind: "restore", Status: "completed", ReceiptId: { } receiptId, Plan: { } plan })
                throw new InvalidOperationException("No database file restoration receipt is available.");
            if (jobs.Values.Any(other => other.Kind == "restore" && other.CreatedAt > job.CreatedAt))
                throw new InvalidOperationException("A newer restore exists. Use its recovery page for PostgreSQL checks.");
            var observation = await recovery.ObserveAsync(plan, receiptId, job.TargetWasPaused, ct);
            await SaveAsync(job with
            {
                Stage = observation.Stage, Observation = observation.Detail, ObservedAt = DateTimeOffset.UtcNow,
                TargetWasPaused = job.TargetWasPaused || observation.Stage == "target-paused"
            });
        }
        finally
        {
            admission.Release();
        }
    }

    public async Task<BackupJobDto> QueueBackupAsync(CreateBackupJobRequest request, CancellationToken ct)
    {
        if (request.Mode is not ("logical" or "physical")) throw new ArgumentException("Unknown backup mode.");
        var type = request.Mode == "logical" ? "full" : BackupTypes.Normalize(request.Type);
        var now = DateTimeOffset.UtcNow;
        var name = BackupTypes.ValidateName(request.Scheduled && string.IsNullOrWhiteSpace(request.Name)
            ? $"scheduled-{type}-{now:yyyyMMdd-HHmmss'Z'}" : request.Name);
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey;

        await admission.WaitAsync(ct);
        try
        {
            if (key != null && jobs.Values.FirstOrDefault(job => job.IdempotencyKey == key) is { } existing)
            {
                if (existing.IncludeOpenBao != request.IncludeOpenBao || existing.IncludeOpenBaoKeys != request.IncludeOpenBaoKeys || existing.IncludeDataProtectionKeys != request.IncludeDataProtectionKeys || existing.Mode != request.Mode || existing.Type != type || (existing.Name != name && !string.IsNullOrWhiteSpace(request.Name))
                    || existing.Scheduled != request.Scheduled || existing.ScheduleKey != request.ScheduleKey)
                {
                    throw new ArgumentException("This idempotency key is already used by a different backup request.");
                }

                return Describe(existing);
            }

            return await EnqueueAsync(new Job(Guid.NewGuid(), "backup", now)
            {
                IncludeOpenBao = request.IncludeOpenBao,
                IncludeOpenBaoKeys = request.IncludeOpenBaoKeys,
                IncludeDataProtectionKeys = request.IncludeDataProtectionKeys,
                Mode = request.Mode,
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
        var progress = new JobProgress(IsRecovery(job) ? Path.Combine(jobsPath, $"{job.Id:N}.log") : null);
        liveProgress[job.Id] = progress;
        try
        {
            if (job.Kind == "backup" && job.Mode == "logical")
            {
                var backup = await logical.CreateAsync(job.Name!, job.Id, job.IncludeOpenBao, job.IncludeOpenBaoKeys, job.IncludeDataProtectionKeys, progress, ct);
                await SaveAsync(jobs[job.Id] with { Label = backup.Label, Backup = backup });
            }
            else if (job.Kind == "backup")
                await RunBackupAsync(jobs[job.Id], progress, ct);
            else if (IsRecovery(job))
                await RunRecoveryAsync(jobs[job.Id], progress, ct);
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
                ? IsRecovery(job) ? "The service stopped during recovery. Inspect the destination before starting another restore."
                    : "The backup service stopped during this job. Check the repository for any completed backup."
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

    private async Task RunRecoveryAsync(Job job, JobProgress progress, CancellationToken ct)
    {
        var plan = job.Plan!;
        if (job.Kind == "restore" && plan.Backup.Mode == "logical")
        {
            await using var lease = new FileStream(Path.Combine(options.StatePath, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow || plan.ManifestHash != await logical.ManifestHashAsync(plan.Backup.Label, ct))
                throw new InvalidOperationException("The logical plan expired or its source changed.");
            await SaveAsync(jobs[job.Id] with { Stage = "restoring" });
            progress.Add("Rechecking bundle integrity and PostgreSQL before restoring all databases.");
            await logical.RestoreAsync(plan.Backup.Label, job.Id, progress, ct);
            progress.Add("All databases restored and SQL checks passed.");
            await SaveAsync(jobs[job.Id] with { Stage = "database-validated", Observation = "All databases restored. Validate application access before enabling writers.", ObservedAt = DateTimeOffset.UtcNow });
            return;
        }
        if (job.Kind == "other")
        {
            await using var lease = new FileStream(Path.Combine(options.StatePath, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (jobs.Values.Any(other => other.Kind == "restore" && other.CreatedAt > GetRecovery(plan.Id)!.CreatedAt))
                throw new InvalidOperationException("A newer restore exists.");
            if (job.CompanionHash != await logical.CompanionHashAsync(plan.Backup.Label, ct))
                throw new InvalidOperationException("The companion bundle changed after confirmation.");
            await SaveAsync(jobs[job.Id] with { Stage = "restoring-other" });
            progress.Add("Verifying companion bundle and restoring selected key files.");
            await logical.RestoreOtherAsync(plan.Backup.Label, job.OtherItems, progress, ct);
            progress.Add("Selected key files restored. Verify permissions and application access before enabling writers.");
            await SaveAsync(jobs[job.Id] with { Stage = "other-restored" });
            return;
        }
        if (job.Kind == "openbao")
        {
            var database = GetRecovery(plan.Id);
            if (database is not { Kind: "restore", Status: "completed", Stage: "database-validated" } ||
                jobs.Values.Any(other => other.Kind == "restore" && other.CreatedAt > database.CreatedAt))
                throw new InvalidOperationException("The database recovery is no longer current.");
            var observed = plan.Backup.Mode == "logical" ? null : await recovery.ObserveAsync(plan, database.ReceiptId ?? throw new InvalidOperationException("Missing receipt."), jobs[plan.Id].TargetWasPaused, ct);
            if (observed is not null && observed.Stage != "database-validated") throw new InvalidOperationException("PostgreSQL recovery is not complete.");
            await SaveAsync(job with { Stage = "restoring-openbao" });
            progress.Add("Verifying the paired snapshot checksum and restoring OpenBao through its API.");
            if (plan.Backup.Mode == "logical") await logical.RestoreOpenBaoAsync(plan.Backup.Label, ct);
            else await openBao.RestoreSnapshotAsync(plan.Backup.Label, ct);
            await SaveAsync(jobs[job.Id] with { Stage = "openbao-restored", Observation = "OpenBao accepted the snapshot. Verify unseal state, authentication and application secrets before starting services.", ObservedAt = DateTimeOffset.UtcNow });
            return;
        }

        if (job.Kind == "rehearsal")
        {
            if (plan.Backup.Mode == "logical") throw new InvalidOperationException("Rehearsal requires a physical plan.");
            await SaveAsync(jobs[job.Id] with { Stage = "rehearsing" });
            progress.Add("Rehearsing the pinned backup and exact recovery target in an isolated cluster.");
            var result = await client.TestRestoreAsync(new ScratchRequest(Path.Combine(Path.GetTempPath(), "froststream-rehearsal"), plan.Backup.Label, settings.ExpectedDatabases)
            { Mode = plan.Mode, Timestamp = plan.Target, MinimumFreeBytes = plan.RequiredBytes }, progress, ct);
            if (!result.CleanedUp) throw new InvalidOperationException("Isolated rehearsal cleanup failed.");
            await SaveAsync(jobs[job.Id] with { Stage = "rehearsed", Observation = "Pinned recovery target and SQL checks passed in isolation. Temporary cluster stopped and removed.", ObservedAt = DateTimeOffset.UtcNow });
            return;
        }
        if (job.Kind == "archive")
        {
            await SaveAsync(job with { Stage = "checking-archive" });
            progress.Add("Retrieving the pinned backup's bounded WAL range.");
            var result = await client.CheckArchiveReadabilityAsync(plan.ArchiveId, plan.Backup.WalStart, plan.Backup.WalStop, ct);
            await SaveAsync(jobs[job.Id] with { Stage = "archive-" + result.Verdict.ToLowerInvariant(), Observation = result.Detail, ObservedAt = result.ObservedAt });
            return;
        }

        if (job.Kind == "checksum")
        {
            const string message = "Checking backup files for the pinned source.";
            await SaveAsync(job with { Stage = "checksumming" });
            progress.Add(message);
            var result = await client.AssessCoverageAsync(plan.Backup.Label, ct);
            var unsupported = result.Reasons.Any(reason => reason.Contains("not supported", StringComparison.Ordinal));
            var stage = result.BackupVerified ? "checksum-passed" : unsupported ? "checksum-unknown" : "checksum-failed";
            var checksumDetail = result.BackupVerified
                ? "pgBackRest checksum verification passed for the pinned backup."
                : string.Join(" ", result.Reasons);
            await SaveAsync(jobs[job.Id] with { Stage = stage, Observation = checksumDetail, ObservedAt = result.ObservedAt });
            return;
        }

        if (job.Kind != "restore") throw new InvalidOperationException("This recovery operation is no longer supported.");
        await SaveAsync(job with { Stage = "validating" });
        progress.Add("Validating the pinned plan and restore destination.");
        var receipt = await recovery.RestoreAsync(plan, job.Rollback, progress,
            () => SaveAsync(jobs[job.Id] with { Stage = "restoring" }), ct);
        const string detail = "Database files restored. PostgreSQL startup, WAL replay and SQL checks are pending.";
        progress.Add(detail);
        await SaveAsync(jobs[job.Id] with { ReceiptId = receipt.Id, Stage = "files-restored", Observation = detail, ObservedAt = receipt.UpdatedAt });
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
                if (job.IncludeOpenBao) await openBao.ExportForBackupAsync(backup.Label, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"PostgreSQL backup {backup.Label} was created, but the OpenBao export failed: {ex.Message}", ex);
            }

            await SaveAsync(job with { Backup = repository.Describe(result.Produced) });
            if (backup.Type != job.Type)
                throw new InvalidOperationException($"pgBackRest created a {backup.Type} backup ({backup.Label}) after a {job.Type} backup was requested.");

            progress.Add("Packaging physical repository, dependency chain, archived WAL and selected recovery artifacts.");
            await logical.CreatePhysicalBundleAsync(repository.Describe(result.Produced), job.Id, options.RepositoryPath,
                job.IncludeOpenBao, job.IncludeOpenBaoKeys, job.IncludeDataProtectionKeys, ct);
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
        if (job.Label?.StartsWith("backup-", StringComparison.Ordinal) == true)
        {
            await logical.VerifyAsync(job.Label, job.Kind == "verify-deep", progress, ct);
            progress.ResultSummary = "Logical backup verification passed.";
            return;
        }
        if (job.Label is not null && logical.FindPhysicalBundle(job.Label) is { } bundle)
        {
            await logical.VerifyAsync(Path.GetFileName(bundle), false, progress, ct);
            var scratch = Path.Combine(Path.GetTempPath(), "physical-bundle-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            var canDeleteScratch = true;
            try
            {
                var bundledClient = new PgBackRestClient(options with
                {
                    RepositoryPath = Path.Combine(bundle, "postgres_physical", "pgbackrest"),
                    StatePath = Path.Combine(scratch, "state"), ConfigPath = Path.Combine(scratch, "pgbackrest.conf")
                });
                await bundledClient.WriteConfigurationAsync(ct);
                await bundledClient.VerifyAsync(job.Label, progress, ct);
                if (job.Kind == "verify-deep")
                {
                    canDeleteScratch = false;
                    var result = await bundledClient.TestRestoreAsync(new ScratchRequest(Path.Combine(scratch, "restore"), job.Label, settings.ExpectedDatabases), progress, ct);
                    canDeleteScratch = result.CleanedUp;
                    if (!result.CleanedUp) throw new InvalidOperationException("Temporary restore cleanup failed.");
                }
                progress.ResultSummary = job.Kind == "verify-deep" ? "Bundle hashes and temporary physical restore passed." : "Bundle hashes and repository checksums passed.";
            }
            finally { if (canDeleteScratch) Directory.Delete(scratch, true); }
            return;
        }
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

    private static bool IsRecovery(Job job) => job.Kind is "restore" or "rehearsal" or "checksum" or "openbao" or "archive" or "other";

    private RecoveryJob DescribeRecovery(Job job) => new(job.Id, job.Kind, job.Status, job.Stage!, job.Plan!, job.CreatedAt,
        job.ReceiptId, job.Observation, job.ObservedAt, job.Error,
        job.Status == "running" && liveProgress.TryGetValue(job.Id, out var progress) ? progress.Lines() : job.Progress);

    private string[] ReadRecoveryLog(Guid id)
    {
        var path = Path.Combine(jobsPath, $"{id:N}.log");
        return File.Exists(path) ? File.ReadLines(path).TakeLast(100).ToArray() : [];
    }

    private sealed record Job(Guid Id, string Kind, DateTimeOffset CreatedAt)
    {
        public string? CompanionHash { get; init; }
        public string[] OtherItems { get; init; } = [];
        public RecoveryPlan? Plan { get; init; }
        public string? Rollback { get; init; }
        public Guid? ReceiptId { get; init; }
        public string? Stage { get; init; }
        public string? Observation { get; init; }
        public DateTimeOffset? ObservedAt { get; init; }
        public bool TargetWasPaused { get; init; }
        public string? ResultSummary { get; init; }
        public string Status { get; init; } = "queued";
        public string? Name { get; init; }
        public bool IncludeOpenBao { get; init; } = true;
        public bool IncludeOpenBaoKeys { get; init; }
        public bool IncludeDataProtectionKeys { get; init; }
        public string Mode { get; init; } = "physical";
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

    private sealed class JobProgress(string? logPath) : IProgress<OutputLine>
    {
        public string? ResultSummary { get; set; }

        private readonly Queue<string> lines = new();
        private int loggedCharacters;

        public void Report(OutputLine value) => Add(value.Text.TrimEnd());

        public void Add(string text)
        {
            lock (lines)
            {
                if (logPath is not null && loggedCharacters < 1024 * 1024)
                {
                    File.AppendAllText(logPath, text + Environment.NewLine);
                    loggedCharacters += text.Length + 1;
                }

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

internal sealed record RecoveryJob(Guid Id, string Kind, string Status, string Stage, RecoveryPlan Plan,
    DateTimeOffset CreatedAt, Guid? ReceiptId, string? Observation, DateTimeOffset? ObservedAt,
    string? Error, IReadOnlyList<string> Progress);
