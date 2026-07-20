using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class DownloadFlowV2Repository(
    DataBridgeDbContext db,
    IClock clock,
    IDownloadJobStateNotifier notifier,
    ILogger<DownloadFlowV2Repository>? logger = null) : IDownloadFlowV2Repository
{
    public static readonly Duration LeaseDuration = Duration.FromSeconds(45);

    public async Task<DownloadRunRequest?> CreateInitialRunAsync(
        DownloadRequested request,
        bool autoStart,
        CancellationToken ct = default)
    {
        var existingJob = await db.DownloadJobs.FirstOrDefaultAsync(x => x.JobId == request.JobId, ct);
        if (await db.DownloadJobHistory.AsNoTracking()
                .AnyAsync(x => x.JobId == request.JobId && x.MessageId == request.MessageId, ct))
            return null;

        var hasPriorRequest = existingJob is not null && await db.DownloadJobHistory.AsNoTracking()
            .AnyAsync(x => x.JobId == request.JobId && x.EventName == nameof(DownloadRequested), ct);

        // Playlist expansion creates the child row and playlist FK atomically before publishing
        // DownloadRequested. Adopt that skeleton here. A later force-request may also start the
        // same stable JobId, but ordinary redelivery must never manufacture another run.
        if (hasPriorRequest && (!request.ForceDownload
                                || existingJob!.Status is not (DownloadJobStatus.Stopped
                                    or DownloadJobStatus.Failed or DownloadJobStatus.Ignored)))
            return null;

        var blockedProvider = autoStart ? await FindOpenProviderCircuitAsync(request.SourceUrl, ct) : null;
        if (blockedProvider is not null)
            autoStart = false;

        var now = clock.GetCurrentInstant();
        var status = autoStart ? DownloadJobStatus.Running : DownloadJobStatus.Stopped;
        var runId = autoStart ? Guid.NewGuid() : (Guid?)null;
        var runNumber = autoStart ? (existingJob?.CurrentRunNumber ?? 0) + 1 : existingJob?.CurrentRunNumber ?? 0;

        var groupKind = request.SourceKind switch
        {
            DownloadSourceKind.Playlist => "playlist",
            DownloadSourceKind.Channel => "channel",
            _ => "direct"
        };
        var groupStatus = autoStart ? "running" : "stopped";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO downloads.download_groups
              (group_id, correlation_id, kind, status, source_url, requested_by, storage_key,
               total_jobs, completed_jobs, warning_jobs, failed_jobs, created_at, updated_at)
            VALUES
              ({request.CorrelationId}, {request.CorrelationId}, CAST({groupKind} AS downloads.download_group_kind),
               CAST({groupStatus} AS downloads.download_group_status), {request.SourceUrl}, {request.RequestedBy},
               {request.StorageKey}, 0, 0, 0, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT (correlation_id) DO NOTHING
            """, ct);

        var previous = existingJob?.Status ?? DownloadJobStatus.Queued;
        var job = existingJob ?? new DownloadJobEntity
        {
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            SourceUrl = request.SourceUrl,
            RequestedBy = request.RequestedBy,
            StorageKey = request.StorageKey,
            SourceKind = request.SourceKind,
            Priority = request.Priority,
            IngestOrigin = IngestOrigin.Download,
            UpdatedAt = now
        };

        job.CorrelationId = request.CorrelationId;
        job.SourceUrl = request.SourceUrl;
        job.RequestedBy = request.RequestedBy ?? job.RequestedBy;
        job.StorageKey = request.StorageKey ?? job.StorageKey;
        job.SourceKind = request.SourceKind;
        job.Priority = request.Priority;
        job.Status = status;
        job.Stage = autoStart ? DownloadStage.Metadata : DownloadStage.None;
        job.StageStatus = autoStart ? DownloadStageStatus.Pending : DownloadStageStatus.Stopped;
        job.CurrentRunId = runId;
        job.CurrentRunNumber = runNumber;
        job.CurrentAttempt = 0;
        job.CurrentArtifactKey = null;
        job.WarningCount = 0;
        job.StopRequestedAt = null;
        job.StopRequestedBy = null;
        job.StopReason = null;
        job.FailureKind = blockedProvider is null ? null : FailureKind.ProviderBlocked;
        job.FailureCode = blockedProvider is null ? null : "provider_circuit_open";
        job.FailureMessage = blockedProvider is null
            ? null
            : $"The provider circuit for '{blockedProvider}' is open. Clear it, then start this job explicitly.";
        job.CompletedAt = null;
        job.State = LegacyState(job.Status, job.Stage, job.StageStatus);
        job.UpdatedAt = now;

        if (existingJob is null)
            db.DownloadJobs.Add(job);

        if (runId is { } id)
        {
            db.DownloadJobRuns.Add(new DownloadJobRunEntity
            {
                RunId = id,
                JobId = request.JobId,
                RunNumber = runNumber,
                Status = DownloadJobStatus.Running,
                Stage = DownloadStage.Metadata,
                StageStatus = DownloadStageStatus.Pending,
                StartedAt = now,
                UpdatedAt = now
            });
        }

        db.DownloadJobHistory.Add(new DownloadJobHistoryEntity
        {
            JobId = request.JobId,
            MessageId = request.MessageId,
            OperationKey = request.OperationKey,
            EventName = nameof(DownloadRequested),
            PayloadJson = JsonSerializer.Serialize(request)
        });

        await db.SaveChangesAsync(ct);
        await NotifyAsync(job, previous, ct);
        await RefreshGroupAggregateAsync(job.CorrelationId, ct);

        return runId is { } createdRunId
            ? new DownloadRunRequest { RunId = createdRunId, RunNumber = runNumber, Request = request }
            : null;
    }

    public async Task<DownloadRunRequest?> StartFreshRunAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        if (job is null || job.Status is not (DownloadJobStatus.Stopped or DownloadJobStatus.Failed))
            return null;

        if (await FindOpenProviderCircuitAsync(job.SourceUrl, ct) is { } blockedProvider)
        {
            job.FailureKind = FailureKind.ProviderBlocked;
            job.FailureCode = "provider_circuit_open";
            job.FailureMessage = $"The provider circuit for '{blockedProvider}' is open. Clear it before starting this job.";
            job.UpdatedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }

        var payload = await db.DownloadJobHistory.AsNoTracking()
            .Where(x => x.JobId == jobId && x.EventName == nameof(DownloadRequested))
            .OrderBy(x => x.RecordedAt)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        var original = JsonSerializer.Deserialize<DownloadRequested>(payload);
        if (original is null)
            return null;

        var previous = job.Status;
        var now = clock.GetCurrentInstant();
        var runId = Guid.NewGuid();
        var runNumber = job.CurrentRunNumber + 1;
        job.Status = DownloadJobStatus.Running;
        job.Stage = DownloadStage.Metadata;
        job.StageStatus = DownloadStageStatus.Pending;
        job.CurrentRunId = runId;
        job.CurrentRunNumber = runNumber;
        job.CurrentAttempt = 0;
        job.CurrentArtifactKey = null;
        job.WarningCount = 0;
        job.StopRequestedAt = null;
        job.StopRequestedBy = null;
        job.StopReason = null;
        job.FailureKind = null;
        job.FailureCode = null;
        job.FailureMessage = null;
        job.TempFileRef = null;
        job.FileSizeBytes = null;
        job.ContentHashXxh128 = null;
        job.StorageVersion = null;
        job.InfoJsonStoragePath = null;
        job.InfoJsonContentHashXxh128 = null;
        job.InfoJsonSizeBytes = null;
        job.MetaStoragePath = null;
        job.CompletedAt = null;
        job.State = DownloadJobState.MetadataPending;
        job.UpdatedAt = now;

        db.DownloadJobRuns.Add(new DownloadJobRunEntity
        {
            RunId = runId,
            JobId = jobId,
            RunNumber = runNumber,
            Status = DownloadJobStatus.Running,
            Stage = DownloadStage.Metadata,
            StageStatus = DownloadStageStatus.Pending,
            StartedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        await RefreshGroupAggregateAsync(job.CorrelationId, ct);

        return new DownloadRunRequest
        {
            RunId = runId,
            RunNumber = runNumber,
            Request = original with
            {
                MessageId = Guid.NewGuid(),
                CausationId = original.MessageId,
                OperationKey = $"job/{jobId:N}/run/{runNumber}/start",
                OccurredAt = now,
                Attempt = 1
            }
        };
    }

    public async Task<DownloadControlDecision> RequestStopAsync(
        Guid jobId,
        string? requestedBy,
        string? reason,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        if (job is null)
            return new DownloadControlDecision(false, false, jobId, null, Guid.Empty, null, "not_found", "Job not found.");

        if (job.Status is DownloadJobStatus.Completed or DownloadJobStatus.CompletedWithWarnings
            or DownloadJobStatus.AlreadyDownloaded or DownloadJobStatus.Ignored or DownloadJobStatus.Failed)
            return new DownloadControlDecision(false, true, jobId, job.CurrentRunId, job.CorrelationId, job.Status,
                "terminal", $"Job cannot be stopped from {job.Status}.");

        if (job.Status is DownloadJobStatus.Stopped or DownloadJobStatus.Stopping)
            return new DownloadControlDecision(true, true, jobId, job.CurrentRunId, job.CorrelationId, job.Status, null, null);

        var previous = job.Status;
        var immediate = job.Status == DownloadJobStatus.Queued || job.CurrentRunId is null;
        job.Status = immediate ? DownloadJobStatus.Stopped : DownloadJobStatus.Stopping;
        job.StageStatus = immediate ? DownloadStageStatus.Stopped : job.StageStatus;
        job.FailureKind = FailureKind.Stopped;
        job.FailureCode = "user_stopped";
        job.FailureMessage = BuildStopMessage(requestedBy, reason);
        job.StopRequestedAt = clock.GetCurrentInstant();
        job.StopRequestedBy = requestedBy;
        job.StopReason = reason;
        job.State = immediate ? DownloadJobState.Cancelled : DownloadJobState.Cancelling;
        job.UpdatedAt = clock.GetCurrentInstant();
        if (immediate)
            job.CompletedAt = job.UpdatedAt;

        if (job.CurrentRunId is { } runId)
        {
            var run = await db.DownloadJobRuns.FirstAsync(x => x.RunId == runId, ct);
            run.Status = job.Status;
            run.FailureKind = FailureKind.Stopped;
            run.FailureCode = job.FailureCode;
            run.FailureMessage = job.FailureMessage;
            run.UpdatedAt = job.UpdatedAt;
            if (immediate)
                run.EndedAt = job.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        if (immediate)
            await RefreshGroupAggregateAsync(job.CorrelationId, ct);
        return new DownloadControlDecision(true, true, jobId, job.CurrentRunId, job.CorrelationId, job.Status, null, null);
    }

    public async Task<IReadOnlyList<DownloadRunRequest>> StartGroupAsync(Guid correlationId, CancellationToken ct = default)
    {
        var ids = await db.DownloadJobs.AsNoTracking()
            .Where(x => x.CorrelationId == correlationId
                        && (x.Status == DownloadJobStatus.Stopped || x.Status == DownloadJobStatus.Failed))
            .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt)
            .Select(x => x.JobId)
            .ToListAsync(ct);
        var runs = new List<DownloadRunRequest>(ids.Count);
        foreach (var id in ids)
            if (await StartFreshRunAsync(id, ct) is { } run)
                runs.Add(run);
        if (runs.Count == 0)
            await RefreshGroupAggregateAsync(correlationId, ct);
        return runs;
    }

    public async Task<IReadOnlyList<DownloadControlDecision>> StopGroupAsync(Guid correlationId, string? requestedBy, string? reason, CancellationToken ct = default)
    {
        var group = await db.DownloadGroups.FirstOrDefaultAsync(x => x.CorrelationId == correlationId, ct);
        if (group is not null)
        {
            group.Status = DownloadGroupStatus.Stopping;
            group.UpdatedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
        }
        var ids = await db.DownloadJobs.AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .Select(x => x.JobId)
            .ToListAsync(ct);
        var decisions = new List<DownloadControlDecision>(ids.Count);
        foreach (var id in ids)
            decisions.Add(await RequestStopAsync(id, requestedBy, reason, ct));
        if (ids.Count == 0 && group is not null)
            await SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Stopped, ct: ct);
        else
            await RefreshGroupAggregateAsync(correlationId, ct);
        return decisions;
    }

    public async Task<bool> BeginStageAttemptAsync(DownloadExecutionIdentity execution, string operationKey, CancellationToken ct = default)
    {
        var currentStatus = await CurrentExecutableStatusAsync(execution.JobId, execution.RunId, execution.Stage, ct);
        if (currentStatus is null)
            return false;

        var artifactKey = NormalizeArtifactKey(execution.ArtifactKey);
        var existing = await db.DownloadStageAttempts.FirstOrDefaultAsync(x =>
            x.RunId == execution.RunId && x.Stage == execution.Stage
            && x.ArtifactKey == artifactKey && x.Attempt == execution.Attempt, ct);
        if (existing is null)
        {
            db.DownloadStageAttempts.Add(new DownloadStageAttemptEntity
            {
                RunId = execution.RunId,
                JobId = execution.JobId,
                Stage = execution.Stage,
                ArtifactKey = artifactKey,
                Attempt = execution.Attempt,
                Status = DownloadStageStatus.Pending,
                DispatchId = execution.DispatchId,
                OperationKey = operationKey,
                UpdatedAt = clock.GetCurrentInstant()
            });
        }
        else if (existing.DispatchId != execution.DispatchId)
        {
            return false;
        }

        return await TransitionAsync(execution.JobId, execution.RunId, currentStatus.Value,
            execution.Stage, DownloadStageStatus.Pending, execution.Attempt, artifactKey, ct);
    }

    public async Task<bool> CompleteStageAttemptAsync(DownloadExecutionIdentity execution, CancellationToken ct = default)
    {
        var row = await CurrentAttemptAsync(execution, ct);
        if (row is null)
            return false;
        row.Status = DownloadStageStatus.Succeeded;
        row.EndedAt = clock.GetCurrentInstant();
        row.UpdatedAt = row.EndedAt.Value;
        await db.SaveChangesAsync(ct);
        var currentStatus = await CurrentExecutableStatusAsync(execution.JobId, execution.RunId, execution.Stage, ct);
        return currentStatus is not null && await TransitionAsync(execution.JobId, execution.RunId, currentStatus.Value,
            execution.Stage, DownloadStageStatus.Succeeded, execution.Attempt, execution.ArtifactKey, ct);
    }

    public async Task<bool> MarkRetryWaitingAsync(DownloadExecutionIdentity execution, FailureKind kind, string? code, string message, CancellationToken ct = default)
    {
        var row = await CurrentAttemptAsync(execution, ct);
        if (row is null)
            return false;
        var now = clock.GetCurrentInstant();
        row.Status = DownloadStageStatus.Failed;
        row.FailureKind = kind;
        row.FailureCode = code;
        row.FailureMessage = message;
        row.EndedAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        var currentStatus = await CurrentExecutableStatusAsync(execution.JobId, execution.RunId, execution.Stage, ct);
        if (currentStatus is null)
            return false;
        return await TransitionAsync(execution.JobId, execution.RunId, currentStatus.Value,
            execution.Stage, DownloadStageStatus.RetryWaiting, execution.Attempt, execution.ArtifactKey, ct);
    }

    public async Task<bool> FailStageAttemptAsync(DownloadExecutionIdentity execution, FailureKind kind, string? code,
        string message, CancellationToken ct = default)
    {
        var row = await CurrentAttemptAsync(execution, ct);
        if (row is null)
            return false;
        var now = clock.GetCurrentInstant();
        row.Status = DownloadStageStatus.Failed;
        row.FailureKind = kind;
        row.FailureCode = code;
        row.FailureMessage = message;
        row.EndedAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        var currentStatus = await CurrentExecutableStatusAsync(execution.JobId, execution.RunId, execution.Stage, ct);
        return currentStatus is not null && await TransitionAsync(execution.JobId, execution.RunId, currentStatus.Value,
            execution.Stage, DownloadStageStatus.Failed, execution.Attempt, execution.ArtifactKey, ct);
    }

    public async Task<bool> TransitionAsync(Guid jobId, Guid runId, DownloadJobStatus status, DownloadStage stage,
        DownloadStageStatus stageStatus, int attempt = 0, string? artifactKey = null, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        if (job is null || job.Status is DownloadJobStatus.Failed or DownloadJobStatus.Stopped
            or DownloadJobStatus.Completed or DownloadJobStatus.CompletedWithWarnings or DownloadJobStatus.AlreadyDownloaded
            || job.CurrentRunId != runId
            || (job.StopRequestedAt is not null && status == DownloadJobStatus.Running))
            return false;

        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == runId && x.JobId == jobId, ct);
        if (run is null)
            return false;

        var previous = job.Status;
        var now = clock.GetCurrentInstant();
        job.Status = status;
        job.Stage = stage;
        job.StageStatus = stageStatus;
        job.CurrentAttempt = attempt;
        job.CurrentArtifactKey = NormalizeArtifactKey(artifactKey) is { Length: > 0 } key ? key : null;
        job.State = LegacyState(status, stage, stageStatus);
        job.UpdatedAt = now;
        run.Status = status;
        run.Stage = stage;
        run.StageStatus = stageStatus;
        run.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        return true;
    }

    public async Task<bool> FailRunAsync(Guid jobId, Guid runId, FailureKind kind, string? code, string message, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == runId && x.JobId == jobId, ct);
        var compensationFailure = string.Equals(code, "compensation_incomplete", StringComparison.Ordinal);
        var leaseInterruption = kind == FailureKind.Interrupted
                                && string.Equals(code, "worker_lease_expired", StringComparison.Ordinal);
        if (job is null || run is null || job.CurrentRunId != runId
            || (job.StopRequestedAt is not null && !compensationFailure && !leaseInterruption)
            || job.Status is DownloadJobStatus.Completed or DownloadJobStatus.CompletedWithWarnings
                or DownloadJobStatus.AlreadyDownloaded or DownloadJobStatus.Stopped
            || (job.Status == DownloadJobStatus.Stopping && !compensationFailure && !leaseInterruption))
            return false;

        var previous = job.Status;
        var now = clock.GetCurrentInstant();
        job.Status = DownloadJobStatus.Failed;
        job.StageStatus = DownloadStageStatus.Failed;
        job.State = DownloadJobState.FailedPermanent;
        job.FailureKind = kind;
        job.FailureCode = code;
        job.FailureMessage = message;
        job.UpdatedAt = now;
        job.CompletedAt = now;
        run.Status = DownloadJobStatus.Failed;
        run.StageStatus = DownloadStageStatus.Failed;
        run.FailureKind = kind;
        run.FailureCode = code;
        run.FailureMessage = message;
        run.UpdatedAt = now;
        run.EndedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        await RefreshGroupAggregateAsync(job.CorrelationId, ct);
        return true;
    }

    public async Task<bool> CompleteRunAsync(Guid jobId, Guid runId, bool withWarnings, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == runId && x.JobId == jobId, ct);
        if (job is null || run is null || job.CurrentRunId != runId || job.StopRequestedAt is not null
            || job.Status != DownloadJobStatus.Running)
            return false;

        var previous = job.Status;
        var now = clock.GetCurrentInstant();
        var status = withWarnings || job.WarningCount > 0
            ? DownloadJobStatus.CompletedWithWarnings
            : DownloadJobStatus.Completed;
        job.Status = status;
        job.Stage = DownloadStage.Finalize;
        job.StageStatus = DownloadStageStatus.Succeeded;
        job.State = DownloadJobState.Completed;
        job.UpdatedAt = now;
        job.CompletedAt = now;
        run.Status = status;
        run.Stage = DownloadStage.Finalize;
        run.StageStatus = DownloadStageStatus.Succeeded;
        run.UpdatedAt = now;
        run.EndedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        await RefreshGroupAggregateAsync(job.CorrelationId, ct);
        return true;
    }

    /// <summary>
    /// Commits the final stage, playlist membership, job, and run in one transaction. Locking the
    /// job row makes this mutually exclusive with Stop: either the final commit wins and Stop sees
    /// a terminal job, or Stop wins and no playlist membership is inserted.
    /// </summary>
    public async Task<bool> FinalizeRunAsync(
        DownloadExecutionIdentity execution,
        Guid mediaGuid,
        string? provider = null,
        string? sourceMediaId = null,
        Instant? sourceLastModified = null,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(execution.JobId, ct);
        if (job is null || job.CurrentRunId != execution.RunId)
            return false;

        if (job.Status is DownloadJobStatus.Completed or DownloadJobStatus.CompletedWithWarnings)
            return true;

        if (job.Status != DownloadJobStatus.Running || job.StopRequestedAt is not null
            || job.Stage != DownloadStage.Finalize || job.CurrentAttempt != execution.Attempt
            || NormalizeArtifactKey(job.CurrentArtifactKey) != NormalizeArtifactKey(execution.ArtifactKey))
            return false;

        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(
            x => x.RunId == execution.RunId && x.JobId == execution.JobId, ct);
        var attempt = await db.DownloadStageAttempts.FirstOrDefaultAsync(
            x => x.DispatchId == execution.DispatchId && x.RunId == execution.RunId
                 && x.JobId == execution.JobId && x.Stage == DownloadStage.Finalize
                 && x.Attempt == execution.Attempt, ct);
        if (run is null || attempt is null
            || attempt.Status is not (DownloadStageStatus.Pending or DownloadStageStatus.Running))
            return false;

        await UpsertSourceMappingAsync(
            execution.JobId, mediaGuid, provider, sourceMediaId, sourceLastModified, ct);
        await LinkPlaylistMembershipIfNeededAsync(execution.JobId, mediaGuid, ct);

        var previous = job.Status;
        var now = clock.GetCurrentInstant();
        var status = job.WarningCount > 0
            ? DownloadJobStatus.CompletedWithWarnings
            : DownloadJobStatus.Completed;
        attempt.Status = DownloadStageStatus.Succeeded;
        attempt.EndedAt = now;
        attempt.UpdatedAt = now;
        job.Status = status;
        job.StageStatus = DownloadStageStatus.Succeeded;
        job.State = DownloadJobState.Completed;
        job.UpdatedAt = now;
        job.CompletedAt = now;
        run.Status = status;
        run.Stage = DownloadStage.Finalize;
        run.StageStatus = DownloadStageStatus.Succeeded;
        run.UpdatedAt = now;
        run.EndedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        await RefreshGroupAggregateAsync(job.CorrelationId, ct);
        return true;
    }

    public async Task<bool> MarkStoppedAsync(Guid jobId, Guid runId, string? message, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == runId && x.JobId == jobId, ct);
        if (job is null || run is null || job.CurrentRunId != runId)
            return false;
        if (job.Status == DownloadJobStatus.Stopped)
            return true;
        if (job.StopRequestedAt is null || job.Status is not (DownloadJobStatus.Running
                or DownloadJobStatus.Stopping or DownloadJobStatus.Compensating))
            return false;
        var previous = job.Status;
        var now = clock.GetCurrentInstant();
        job.Status = DownloadJobStatus.Stopped;
        job.StageStatus = DownloadStageStatus.Stopped;
        job.State = DownloadJobState.Cancelled;
        job.FailureKind = FailureKind.Stopped;
        job.FailureCode = "user_stopped";
        job.FailureMessage = string.IsNullOrWhiteSpace(message) ? "Download stopped by request." : message;
        job.UpdatedAt = now;
        job.CompletedAt = now;
        run.Status = DownloadJobStatus.Stopped;
        run.StageStatus = DownloadStageStatus.Stopped;
        run.FailureKind = FailureKind.Stopped;
        run.FailureCode = job.FailureCode;
        run.FailureMessage = job.FailureMessage;
        run.UpdatedAt = now;
        run.EndedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        await RefreshGroupAggregateAsync(job.CorrelationId, ct);
        return true;
    }

    public async Task<bool> MarkAlreadyDownloadedAsync(
        Guid jobId,
        Guid runId,
        Guid mediaGuid,
        string? provider,
        string? sourceMediaId,
        Instant? sourceLastModified = null,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == runId && x.JobId == jobId, ct);
        if (job is null || run is null || job.CurrentRunId != runId)
            return false;
        if (job.Status == DownloadJobStatus.AlreadyDownloaded)
            return true;
        if (job.Status != DownloadJobStatus.Running || job.StopRequestedAt is not null)
            return false;

        var latest = await db.MediaContentIdVersions.AsNoTracking()
            .Where(x => x.MediaGuid == mediaGuid)
            .OrderByDescending(x => x.VersionNum)
            .FirstOrDefaultAsync(ct);
        await UpsertSourceMappingAsync(jobId, mediaGuid, provider, sourceMediaId, sourceLastModified, ct);
        await LinkPlaylistMembershipIfNeededAsync(jobId, mediaGuid, ct);

        var previous = job.Status;
        var now = clock.GetCurrentInstant();
        job.Status = DownloadJobStatus.AlreadyDownloaded;
        job.Stage = DownloadStage.Finalize;
        job.StageStatus = DownloadStageStatus.Skipped;
        job.State = DownloadJobState.AlreadyDownloaded;
        if (latest is not null)
        {
            job.StorageKey = latest.StorageKey;
            job.ContentHashXxh128 = latest.ContentHashXxh128;
        }
        job.UpdatedAt = now;
        job.CompletedAt = now;
        run.Status = DownloadJobStatus.AlreadyDownloaded;
        run.Stage = DownloadStage.Finalize;
        run.StageStatus = DownloadStageStatus.Skipped;
        run.UpdatedAt = now;
        run.EndedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, previous, ct);
        await RefreshGroupAggregateAsync(job.CorrelationId, ct);
        return true;
    }

    public Task<bool> IsStopRequestedAsync(Guid jobId, Guid runId, CancellationToken ct = default)
        => db.DownloadJobs.AsNoTracking().AnyAsync(x => x.JobId == jobId && x.CurrentRunId == runId
            && x.StopRequestedAt != null, ct);

    public async Task RecordWarningAsync(Guid jobId, Guid runId, DownloadStage stage, string? artifactKey,
        string code, string message, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstOrDefaultAsync(x => x.JobId == jobId && x.CurrentRunId == runId, ct);
        if (job is null)
            return;
        var previous = job.Status;
        db.DownloadJobWarnings.Add(new DownloadJobWarningEntity
        {
            JobId = jobId,
            RunId = runId,
            Stage = stage,
            ArtifactKey = NormalizeArtifactKey(artifactKey),
            WarningCode = code,
            WarningMessage = message
        });
        job.WarningCount++;
        job.Stage = stage;
        job.StageStatus = DownloadStageStatus.Warning;
        job.UpdatedAt = clock.GetCurrentInstant();
        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == runId && x.JobId == jobId, ct);
        if (run is not null)
        {
            run.Stage = stage;
            run.StageStatus = DownloadStageStatus.Warning;
            run.UpdatedAt = job.UpdatedAt;
        }
        await db.SaveChangesAsync(ct);
        await NotifyAsync(job, previous, ct);
    }

    public async Task UpsertArtifactAsync(DownloadArtifactSnapshot artifact, CancellationToken ct = default)
    {
        var key = NormalizeArtifactKey(artifact.ArtifactKey);
        var row = await db.DownloadArtifacts.FirstOrDefaultAsync(x => x.RunId == artifact.RunId && x.ArtifactKey == key, ct);
        if (row is null)
        {
            row = new DownloadArtifactEntity
            {
                JobId = artifact.JobId,
                RunId = artifact.RunId,
                Stage = artifact.Stage,
                ArtifactKey = key,
                Kind = artifact.Kind,
                Required = artifact.Required,
                Status = artifact.Status
            };
            db.DownloadArtifacts.Add(row);
        }
        row.Status = artifact.Status;
        row.TempFileRef = artifact.TempFileRef;
        row.StorageKey = artifact.StorageKey;
        row.StoragePath = artifact.StoragePath;
        row.StorageVersion = artifact.StorageVersion;
        row.ContentHashXxh128 = artifact.ContentHashXxh128;
        row.SizeBytes = artifact.SizeBytes;
        row.WarningCode = artifact.WarningCode;
        row.WarningMessage = artifact.WarningMessage;
        row.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DownloadArtifactSnapshot>> ListCompensatableArtifactsAsync(
        Guid jobId, Guid runId, CancellationToken ct = default)
        => await db.DownloadArtifacts.AsNoTracking()
            .Where(x => x.JobId == jobId && x.RunId == runId
                        && x.StorageKey != null && x.StoragePath != null
                        && (x.Status == DownloadArtifactStatus.Pending
                            || x.Status == DownloadArtifactStatus.Uploading
                            || x.Status == DownloadArtifactStatus.Stored
                            || x.Status == DownloadArtifactStatus.Failed
                            || x.Status == DownloadArtifactStatus.Warning
                            || x.Status == DownloadArtifactStatus.Residual))
            .OrderByDescending(x => x.Required)
            .ThenBy(x => x.ArtifactKey)
            .Select(x => new DownloadArtifactSnapshot
            {
                JobId = x.JobId,
                RunId = x.RunId,
                Stage = x.Stage,
                ArtifactKey = x.ArtifactKey,
                Kind = x.Kind,
                Required = x.Required,
                Status = x.Status,
                TempFileRef = x.TempFileRef,
                StorageKey = x.StorageKey,
                StoragePath = x.StoragePath,
                StorageVersion = x.StorageVersion,
                ContentHashXxh128 = x.ContentHashXxh128,
                SizeBytes = x.SizeBytes,
                WarningCode = x.WarningCode,
                WarningMessage = x.WarningMessage
            }).ToListAsync(ct);

    public async Task<AcquireDownloadLeaseResponse> TryAcquireLeaseAsync(AcquireDownloadLeaseRequest request, CancellationToken ct = default)
    {
        var execution = request.Execution;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(execution.JobId, ct);
        var existingLease = await db.DownloadWorkerLeases
            .FirstOrDefaultAsync(x => x.DispatchId == execution.DispatchId, ct);
        if (existingLease is not null)
        {
            // The grant response can be lost after the lease commits (reply timeout, transport
            // hiccup). Re-granting the same worker's active lease makes the retry idempotent
            // instead of stranding a lease nobody heartbeats until the expiry sweep kills the run.
            var regrantNow = clock.GetCurrentInstant();
            if (existingLease.WorkerInstanceId == request.WorkerInstanceId
                && existingLease.Status == DownloadWorkerLeaseStatus.Active
                && existingLease.ExpiresAt > regrantNow)
            {
                existingLease.LastHeartbeatAt = regrantNow;
                existingLease.ExpiresAt = regrantNow + LeaseDuration;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new AcquireDownloadLeaseResponse
                {
                    Granted = true,
                    StopRequested = job?.StopRequestedAt is not null
                                    && execution.Stage is not (DownloadStage.Cleanup or DownloadStage.Compensation),
                    ExpiresAt = existingLease.ExpiresAt
                };
            }
            logger?.LogInformation(
                "Rejected Download V2 lease acquire for DispatchId {DispatchId} JobId {JobId} RunId {RunId} Stage {Stage} Attempt {Attempt}: dispatch already claimed by Worker {LeaseWorker} (Status {LeaseStatus}).",
                execution.DispatchId,
                execution.JobId,
                execution.RunId,
                execution.Stage,
                execution.Attempt,
                existingLease.WorkerInstanceId,
                existingLease.Status);
            return new AcquireDownloadLeaseResponse { Granted = false, RejectionCode = "dispatch_already_claimed" };
        }

        var attempt = await db.DownloadStageAttempts.FirstOrDefaultAsync(x => x.DispatchId == execution.DispatchId, ct);
        if (job is null || attempt is null || job.CurrentRunId != execution.RunId
            || job.Status is not (DownloadJobStatus.Running or DownloadJobStatus.Compensating
                or DownloadJobStatus.Stopping)
            || job.Stage != execution.Stage
            || job.CurrentAttempt != execution.Attempt
            || NormalizeArtifactKey(job.CurrentArtifactKey) != NormalizeArtifactKey(execution.ArtifactKey))
        {
            logger?.LogInformation(
                "Rejected Download V2 lease acquire for DispatchId {DispatchId} JobId {JobId} RunId {RunId} Stage {Stage} Attempt {Attempt}: stale execution. CurrentJobRunId={CurrentRunId} CurrentStatus={CurrentStatus} CurrentStage={CurrentStage} CurrentAttempt={CurrentAttempt} CurrentArtifactKey={CurrentArtifactKey} AttemptExists={AttemptExists} Worker={WorkerInstanceId}.",
                execution.DispatchId,
                execution.JobId,
                execution.RunId,
                execution.Stage,
                execution.Attempt,
                job?.CurrentRunId,
                job?.Status,
                job?.Stage,
                job?.CurrentAttempt,
                job?.CurrentArtifactKey,
                attempt is not null,
                request.WorkerInstanceId);
            return new AcquireDownloadLeaseResponse { Granted = false, RejectionCode = "stale_execution" };
        }

        var now = clock.GetCurrentInstant();
        var expires = now + LeaseDuration;
        db.DownloadWorkerLeases.Add(new DownloadWorkerLeaseEntity
        {
            DispatchId = execution.DispatchId,
            RunId = execution.RunId,
            JobId = execution.JobId,
            Stage = execution.Stage,
            ArtifactKey = NormalizeArtifactKey(execution.ArtifactKey),
            Attempt = execution.Attempt,
            WorkerInstanceId = request.WorkerInstanceId,
            Status = DownloadWorkerLeaseStatus.Active,
            AcquiredAt = now,
            LastHeartbeatAt = now,
            ExpiresAt = expires
        });
        attempt.Status = DownloadStageStatus.Running;
        attempt.StartedAt = now;
        attempt.UpdatedAt = now;
        job.StageStatus = DownloadStageStatus.Running;
        job.UpdatedAt = now;
        var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == execution.RunId, ct);
        if (run is not null)
        {
            run.StageStatus = DownloadStageStatus.Running;
            run.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyAsync(job, job.Status, ct);
        return new AcquireDownloadLeaseResponse
        {
            Granted = true,
            StopRequested = job.StopRequestedAt is not null
                            && execution.Stage is not (DownloadStage.Cleanup or DownloadStage.Compensation),
            ExpiresAt = expires
        };
    }

    public async Task<RenewDownloadLeaseResponse> TryRenewLeaseAsync(RenewDownloadLeaseRequest request, CancellationToken ct = default)
    {
        var lease = await db.DownloadWorkerLeases.FirstOrDefaultAsync(x => x.DispatchId == request.DispatchId, ct);
        var now = clock.GetCurrentInstant();
        if (lease is null || lease.RunId != request.RunId || lease.WorkerInstanceId != request.WorkerInstanceId
            || lease.Status != DownloadWorkerLeaseStatus.Active || lease.ExpiresAt <= now)
            return new RenewDownloadLeaseResponse { Renewed = false };

        var current = await CurrentExecutableStatusAsync(lease.JobId, lease.RunId, lease.Stage, ct) is not null;
        if (!current)
            return new RenewDownloadLeaseResponse { Renewed = false };

        lease.LastHeartbeatAt = now;
        lease.ExpiresAt = now + LeaseDuration;
        await db.SaveChangesAsync(ct);
        return new RenewDownloadLeaseResponse { Renewed = true, ExpiresAt = lease.ExpiresAt };
    }

    public async Task ReleaseLeaseAsync(Guid dispatchId, DownloadWorkerLeaseStatus status, CancellationToken ct = default)
    {
        var lease = await db.DownloadWorkerLeases.FirstOrDefaultAsync(x => x.DispatchId == dispatchId, ct);
        if (lease is null || lease.Status != DownloadWorkerLeaseStatus.Active)
            return;
        lease.Status = status;
        lease.ReleasedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> CanAcceptWorkerEventAsync(DownloadExecutionIdentity execution, CancellationToken ct = default)
    {
        var artifactKey = NormalizeArtifactKey(execution.ArtifactKey);
        var now = clock.GetCurrentInstant();
        return await db.DownloadJobs.AsNoTracking().AnyAsync(x => x.JobId == execution.JobId
                   && x.CurrentRunId == execution.RunId
                   && (x.Status == DownloadJobStatus.Running || x.Status == DownloadJobStatus.Compensating
                       || x.Status == DownloadJobStatus.Stopping)
                   && x.Stage == execution.Stage
                   && x.CurrentAttempt == execution.Attempt
                   && (x.CurrentArtifactKey ?? "") == artifactKey, ct)
               && await db.DownloadStageAttempts.AsNoTracking().AnyAsync(x => x.DispatchId == execution.DispatchId
                   && x.RunId == execution.RunId && x.JobId == execution.JobId && x.Stage == execution.Stage
                   && x.Attempt == execution.Attempt && x.ArtifactKey == artifactKey
                   && x.Status == DownloadStageStatus.Running, ct)
               && await db.DownloadWorkerLeases.AsNoTracking().AnyAsync(x => x.DispatchId == execution.DispatchId
                   && x.RunId == execution.RunId && x.JobId == execution.JobId
                   && ((x.Status == DownloadWorkerLeaseStatus.Active && x.ExpiresAt > now)
                       || x.Status == DownloadWorkerLeaseStatus.Released
                       || x.Status == DownloadWorkerLeaseStatus.Stopped), ct);
    }

    public async Task<IReadOnlyList<ExpiredDownloadLease>> FailExpiredLeasesAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var leases = await db.DownloadWorkerLeases
            .Where(x => x.Status == DownloadWorkerLeaseStatus.Active && x.ExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var lease in leases)
        {
            lease.Status = DownloadWorkerLeaseStatus.Expired;
            lease.ReleasedAt = now;

            // An upload may have reached the backend before its Worker disappeared. We cannot
            // prove absence, so retain an explicit residual record for manual cleanup instead of
            // silently treating the object as removed.
            var uncertainArtifacts = await db.DownloadArtifacts
                .Where(x => x.JobId == lease.JobId && x.RunId == lease.RunId
                            && x.Status == DownloadArtifactStatus.Uploading)
                .ToListAsync(ct);
            foreach (var artifact in uncertainArtifacts)
            {
                artifact.Status = DownloadArtifactStatus.Residual;
                artifact.WarningCode = "worker_lease_expired";
                artifact.WarningMessage = "The Worker lease expired while this object might have been written.";
                artifact.UpdatedAt = now;
                db.DownloadJobWarnings.Add(new DownloadJobWarningEntity
                {
                    JobId = lease.JobId,
                    RunId = lease.RunId,
                    Stage = artifact.Stage,
                    ArtifactKey = artifact.ArtifactKey,
                    WarningCode = "worker_lease_expired",
                    WarningMessage = artifact.WarningMessage
                });
            }
            if (uncertainArtifacts.Count > 0)
            {
                var job = await db.DownloadJobs.FirstOrDefaultAsync(x => x.JobId == lease.JobId, ct);
                if (job is not null)
                    job.WarningCount += uncertainArtifacts.Count;
            }
            await FailRunAsync(lease.JobId, lease.RunId, FailureKind.Interrupted, "worker_lease_expired",
                $"Worker '{lease.WorkerInstanceId}' stopped heartbeating during {lease.Stage}.", ct);
        }
        await db.SaveChangesAsync(ct);
        return leases.Select(x => new ExpiredDownloadLease(x.JobId, x.RunId, x.DispatchId)).ToArray();
    }

    public async Task<IReadOnlyList<ActiveDownloadRun>> ListActiveRunsAsync(Duration minAge, CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant() - minAge;
        return await db.DownloadJobs.AsNoTracking()
            .Where(x => x.CurrentRunId != null
                        && (x.Status == DownloadJobStatus.Running || x.Status == DownloadJobStatus.Stopping
                            || x.Status == DownloadJobStatus.Compensating)
                        && x.UpdatedAt <= cutoff)
            .Select(x => new ActiveDownloadRun(x.JobId, x.CurrentRunId!.Value))
            .ToListAsync(ct);
    }

    public async Task<StartupReconciliationResult> ReconcileForStartupAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var notifications = new List<(DownloadJobEntity Job, DownloadJobStatus Previous)>();
        var queued = await db.DownloadJobs.Where(x => x.Status == DownloadJobStatus.Queued).ToListAsync(ct);
        foreach (var job in queued)
        {
            var previous = job.Status;
            job.Status = DownloadJobStatus.Stopped;
            job.StageStatus = DownloadStageStatus.Stopped;
            job.State = DownloadJobState.Cancelled;
            job.FailureKind = FailureKind.Interrupted;
            job.FailureCode = "service_restarted_before_start";
            job.FailureMessage = "The service restarted before this job began. Start it when ready.";
            job.UpdatedAt = now;
            job.CompletedAt = now;
            notifications.Add((job, previous));
        }

        var active = await db.DownloadJobs.Where(x => x.Status == DownloadJobStatus.Running
            || x.Status == DownloadJobStatus.Stopping || x.Status == DownloadJobStatus.Compensating).ToListAsync(ct);
        foreach (var job in active)
        {
            var previous = job.Status;
            job.Status = DownloadJobStatus.Failed;
            job.StageStatus = DownloadStageStatus.Failed;
            job.State = DownloadJobState.FailedPermanent;
            job.FailureKind = FailureKind.Interrupted;
            job.FailureCode = "service_restarted";
            job.FailureMessage = "The coordinating service restarted while this run was active. Start the job to create a fresh run.";
            job.UpdatedAt = now;
            job.CompletedAt = now;
            if (job.CurrentRunId is { } runId)
            {
                var run = await db.DownloadJobRuns.FirstOrDefaultAsync(x => x.RunId == runId, ct);
                if (run is not null)
                {
                    run.Status = DownloadJobStatus.Failed;
                    run.StageStatus = DownloadStageStatus.Failed;
                    run.FailureKind = FailureKind.Interrupted;
                    run.FailureCode = job.FailureCode;
                    run.FailureMessage = job.FailureMessage;
                    run.UpdatedAt = now;
                    run.EndedAt = now;
                }
            }
            notifications.Add((job, previous));
        }

        var activeRunIds = active.Where(x => x.CurrentRunId is not null).Select(x => x.CurrentRunId!.Value).ToArray();
        if (activeRunIds.Length > 0)
        {
            var attempts = await db.DownloadStageAttempts
                .Where(x => activeRunIds.Contains(x.RunId)
                            && (x.Status == DownloadStageStatus.Pending
                                || x.Status == DownloadStageStatus.Running
                                || x.Status == DownloadStageStatus.RetryWaiting))
                .ToListAsync(ct);
            foreach (var attempt in attempts)
            {
                attempt.Status = DownloadStageStatus.Failed;
                attempt.FailureKind = FailureKind.Interrupted;
                attempt.FailureCode = "service_restarted";
                attempt.FailureMessage = "The coordinating service restarted during this attempt.";
                attempt.UpdatedAt = now;
                attempt.EndedAt = now;
            }
        }

        var activeGroups = await db.DownloadGroups.Where(x => x.Status == DownloadGroupStatus.Queued
            || x.Status == DownloadGroupStatus.Expanding || x.Status == DownloadGroupStatus.Running
            || x.Status == DownloadGroupStatus.Stopping).ToListAsync(ct);
        foreach (var group in activeGroups)
        {
            group.Status = DownloadGroupStatus.Failed;
            group.FailureCode = "service_restarted";
            group.FailureMessage = "The coordinating service restarted while this group was active. Start eligible child jobs explicitly.";
            group.UpdatedAt = now;
            group.CompletedAt = now;
        }

        var activeLeases = await db.DownloadWorkerLeases.Where(x => x.Status == DownloadWorkerLeaseStatus.Active).ToListAsync(ct);
        foreach (var lease in activeLeases)
        {
            lease.Status = DownloadWorkerLeaseStatus.Expired;
            lease.ReleasedAt = now;
        }
        await db.SaveChangesAsync(ct);
        foreach (var (job, previous) in notifications)
            await NotifyAsync(job, previous, ct);
        foreach (var correlationId in notifications.Select(x => x.Job.CorrelationId).Distinct())
            await RefreshGroupAggregateAsync(correlationId, ct);
        return new StartupReconciliationResult(queued.Count, active.Count, activeLeases.Count, activeGroups.Count);
    }

    public async Task CreateGroupIfMissingAsync(DownloadGroupRequested request, CancellationToken ct = default)
    {
        if (await db.DownloadGroups.AnyAsync(x => x.GroupId == request.GroupId, ct))
            return;
        db.DownloadGroups.Add(new DownloadGroupEntity
        {
            GroupId = request.GroupId,
            CorrelationId = request.CorrelationId,
            Kind = request.Kind,
            Status = DownloadGroupStatus.Queued,
            SourceUrl = request.SourceUrl,
            RequestedBy = request.RequestedBy,
            StorageKey = request.StorageKey,
            UpdatedAt = clock.GetCurrentInstant()
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetGroupStatusAsync(Guid groupId, DownloadGroupStatus status, string? failureCode = null,
        string? failureMessage = null, CancellationToken ct = default)
    {
        var group = await db.DownloadGroups.FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        if (group is null)
            return;
        group.Status = status;
        group.FailureCode = failureCode;
        group.FailureMessage = failureMessage;
        group.UpdatedAt = clock.GetCurrentInstant();
        if (status is DownloadGroupStatus.Completed or DownloadGroupStatus.CompletedWithWarnings
            or DownloadGroupStatus.CompletedWithFailures or DownloadGroupStatus.Failed or DownloadGroupStatus.Stopped)
            group.CompletedAt = group.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task RefreshGroupAggregateAsync(Guid correlationId, CancellationToken ct = default)
    {
        var group = await db.DownloadGroups.FirstOrDefaultAsync(x => x.CorrelationId == correlationId, ct);
        if (group is null)
            return;
        var stopInProgress = group.Status == DownloadGroupStatus.Stopping;
        var children = await db.DownloadJobs.AsNoTracking().Where(x => x.CorrelationId == correlationId)
            .Select(x => new { x.Status, x.WarningCount }).ToListAsync(ct);
        var statuses = children.Select(x => x.Status).ToList();
        group.TotalJobs = children.Count;
        group.CompletedJobs = children.Count(x => x.Status is DownloadJobStatus.Completed
            or DownloadJobStatus.CompletedWithWarnings or DownloadJobStatus.AlreadyDownloaded);
        group.WarningJobs = children.Count(x => x.Status == DownloadJobStatus.CompletedWithWarnings || x.WarningCount > 0);
        group.FailedJobs = children.Count(x => x.Status == DownloadJobStatus.Failed);
        var terminalCount = statuses.Count(x => x is DownloadJobStatus.Completed or DownloadJobStatus.CompletedWithWarnings
            or DownloadJobStatus.AlreadyDownloaded or DownloadJobStatus.Ignored or DownloadJobStatus.Failed or DownloadJobStatus.Stopped);
        if (children.Count > 0 && terminalCount == children.Count)
        {
            group.Status = stopInProgress && statuses.Any(x => x == DownloadJobStatus.Stopped)
                ? DownloadGroupStatus.Stopped
                : group.FailedJobs > 0 ? DownloadGroupStatus.CompletedWithFailures
                : group.WarningJobs > 0 ? DownloadGroupStatus.CompletedWithWarnings
                : statuses.All(x => x == DownloadJobStatus.Stopped) ? DownloadGroupStatus.Stopped
                : DownloadGroupStatus.Completed;
            group.CompletedAt = clock.GetCurrentInstant();
        }
        else if (children.Count > 0 && !stopInProgress)
        {
            group.Status = DownloadGroupStatus.Running;
            group.CompletedAt = null;
        }
        group.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> IsGroupExpansionAllowedAsync(Guid correlationId, CancellationToken ct = default)
        => db.DownloadGroups.AsNoTracking().AnyAsync(x => x.CorrelationId == correlationId
            && x.Status != DownloadGroupStatus.Stopping
            && x.Status != DownloadGroupStatus.Stopped
            && x.Status != DownloadGroupStatus.Failed, ct);

    public async Task<bool> CanAcceptGroupChildAsync(Guid correlationId, CancellationToken ct = default)
    {
        var status = await db.DownloadGroups.AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .Select(x => (DownloadGroupStatus?)x.Status)
            .FirstOrDefaultAsync(ct);
        return status is null || status is not (DownloadGroupStatus.Stopping
            or DownloadGroupStatus.Stopped or DownloadGroupStatus.Failed);
    }

    public async Task OpenProviderCircuitAsync(string provider, string reason, CancellationToken ct = default)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (provider.Length == 0)
            return;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO downloads.download_provider_circuits (provider, is_open, reason, opened_at, cleared_at)
            VALUES ({provider}, TRUE, {reason}, CURRENT_TIMESTAMP, NULL)
            ON CONFLICT (provider) DO UPDATE
            SET is_open = TRUE, reason = EXCLUDED.reason, opened_at = CURRENT_TIMESTAMP, cleared_at = NULL
            """, ct);

        var queued = await db.DownloadJobs
            .Where(x => x.Status == DownloadJobStatus.Queued)
            .ToListAsync(ct);
        foreach (var job in queued.Where(x => ProviderMatchesSource(provider, x.SourceUrl)))
        {
            var previous = job.Status;
            job.Status = DownloadJobStatus.Stopped;
            job.StageStatus = DownloadStageStatus.Stopped;
            job.State = DownloadJobState.Cancelled;
            job.FailureKind = FailureKind.ProviderBlocked;
            job.FailureCode = "provider_circuit_open";
            job.FailureMessage = $"The provider circuit for '{provider}' is open. {reason}";
            job.UpdatedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            await NotifyAsync(job, previous, ct);
            await RefreshGroupAggregateAsync(job.CorrelationId, ct);
        }
    }

    public async Task ClearProviderCircuitAsync(string provider, CancellationToken ct = default)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (provider.Length == 0)
            throw new ArgumentException("Provider is required.", nameof(provider));

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO downloads.download_provider_circuits
                (provider, is_open, reason, opened_at, cleared_at)
            VALUES ({provider}, FALSE, NULL, NULL, CURRENT_TIMESTAMP)
            ON CONFLICT (provider) DO UPDATE
            SET is_open = FALSE, cleared_at = CURRENT_TIMESTAMP
            """, ct);
    }

    public async Task<string?> FindOpenProviderCircuitAsync(string sourceUrl, CancellationToken ct = default)
    {
        var providers = await db.Database.SqlQuery<string>($"""
                SELECT provider AS "Value"
                FROM downloads.download_provider_circuits
                WHERE is_open = TRUE
                """)
            .ToListAsync(ct);
        return providers.FirstOrDefault(provider => ProviderMatchesSource(provider, sourceUrl));
    }

    private Task<DownloadJobEntity?> LockJobAsync(Guid jobId, CancellationToken ct)
        => db.DownloadJobs
            .FromSqlInterpolated($"SELECT * FROM downloads.download_jobs WHERE job_id = {jobId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    private async Task LinkPlaylistMembershipIfNeededAsync(Guid jobId, Guid mediaGuid, CancellationToken ct)
    {
        var item = await db.PlaylistItems.AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => new { x.PlaylistId, x.PlaylistIndex })
            .FirstOrDefaultAsync(ct);
        if (item is null || await db.MediaPlaylistMemberships.AnyAsync(
                x => x.PlaylistId == item.PlaylistId && x.PlaylistIndex == item.PlaylistIndex, ct))
            return;

        db.MediaPlaylistMemberships.Add(new MediaPlaylistMembershipEntity
        {
            MediaGuid = mediaGuid,
            PlaylistId = item.PlaylistId,
            PlaylistIndex = item.PlaylistIndex
        });
    }

    private async Task UpsertSourceMappingAsync(
        Guid jobId,
        Guid mediaGuid,
        string? provider,
        string? sourceMediaId,
        Instant? sourceLastModified,
        CancellationToken ct)
    {
        provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
        sourceMediaId = string.IsNullOrWhiteSpace(sourceMediaId) ? null : sourceMediaId.Trim();
        if (provider is null || sourceMediaId is null)
            return;

        var source = await db.MediaSourceVersions.FirstOrDefaultAsync(
            x => x.Provider == provider && x.SourceMediaId == sourceMediaId, ct);
        if (source is null)
        {
            db.MediaSourceVersions.Add(new MediaSourceVersionEntity
            {
                Provider = provider,
                SourceMediaId = sourceMediaId,
                SourceLastModified = sourceLastModified,
                MediaGuid = mediaGuid,
                LatestJobId = jobId
            });
            return;
        }

        source.MediaGuid = mediaGuid;
        source.SourceLastModified = sourceLastModified ?? source.SourceLastModified;
        source.LatestJobId = jobId;
    }

    private async Task<DownloadStageAttemptEntity?> CurrentAttemptAsync(DownloadExecutionIdentity execution, CancellationToken ct)
    {
        if (await CurrentExecutableStatusAsync(execution.JobId, execution.RunId, execution.Stage, ct) is null)
            return null;
        var artifactKey = NormalizeArtifactKey(execution.ArtifactKey);
        var isCurrent = await db.DownloadJobs.AsNoTracking().AnyAsync(x =>
            x.JobId == execution.JobId
            && x.CurrentRunId == execution.RunId
            && x.Stage == execution.Stage
            && x.CurrentAttempt == execution.Attempt
            && (x.CurrentArtifactKey ?? "") == artifactKey, ct);
        if (!isCurrent)
            return null;
        return await db.DownloadStageAttempts.FirstOrDefaultAsync(x => x.DispatchId == execution.DispatchId
            && x.RunId == execution.RunId && x.JobId == execution.JobId && x.Stage == execution.Stage
            && x.Attempt == execution.Attempt && x.ArtifactKey == artifactKey, ct);
    }

    private Task<DownloadJobStatus?> CurrentExecutableStatusAsync(
        Guid jobId, Guid runId, DownloadStage stage, CancellationToken ct)
        => db.DownloadJobs.AsNoTracking()
            .Where(x => x.JobId == jobId && x.CurrentRunId == runId
                        && (x.Status == DownloadJobStatus.Running || x.Status == DownloadJobStatus.Compensating
                            || (x.Status == DownloadJobStatus.Stopping
                                && (stage == DownloadStage.Cleanup || stage == DownloadStage.Compensation))))
            .Select(x => (DownloadJobStatus?)x.Status)
            .FirstOrDefaultAsync(ct);

    private Task NotifyAsync(DownloadJobEntity job, DownloadJobStatus previous, CancellationToken ct)
        => notifier.NotifyV2Async(job.JobId, job.CorrelationId, job.Status, previous, job.Stage, job.StageStatus,
            job.CurrentRunId, job.CurrentRunNumber, job.CurrentAttempt, job.CurrentArtifactKey, job.WarningCount, ct);

    private static string NormalizeArtifactKey(string? value) => value?.Trim() ?? string.Empty;

    private static bool ProviderMatchesSource(string provider, string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            return sourceUrl.Contains(provider, StringComparison.OrdinalIgnoreCase);
        var normalizedProvider = provider.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        var host = uri.Host.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return host.Contains(normalizedProvider, StringComparison.OrdinalIgnoreCase)
               || normalizedProvider.Contains(host.Split('.')[0], StringComparison.OrdinalIgnoreCase);
    }

    private static DownloadJobState LegacyState(DownloadJobStatus status, DownloadStage stage, DownloadStageStatus stageStatus)
        => status switch
        {
            DownloadJobStatus.Queued => DownloadJobState.Queued,
            DownloadJobStatus.Stopping => DownloadJobState.Cancelling,
            DownloadJobStatus.Stopped => DownloadJobState.Cancelled,
            DownloadJobStatus.Compensating => DownloadJobState.Compensating,
            DownloadJobStatus.Completed or DownloadJobStatus.CompletedWithWarnings => DownloadJobState.Completed,
            DownloadJobStatus.Failed => DownloadJobState.FailedPermanent,
            DownloadJobStatus.AlreadyDownloaded => DownloadJobState.AlreadyDownloaded,
            DownloadJobStatus.Ignored => DownloadJobState.Ignored,
            _ => stage switch
            {
                DownloadStage.Metadata => DownloadJobState.MetadataPending,
                DownloadStage.DuplicateCheck => DownloadJobState.MetadataResolved,
                DownloadStage.WaitingForWorker => DownloadJobState.DownloadQueued,
                DownloadStage.MediaAcquire => DownloadJobState.DownloadPending,
                DownloadStage.PrimaryMediaUpload or DownloadStage.MetaSidecarUpload or DownloadStage.InfoJsonUpload
                    or DownloadStage.ThumbnailUpload or DownloadStage.CaptionUpload => DownloadJobState.UploadPending,
                DownloadStage.Finalize => DownloadJobState.CommitPending,
                _ => stageStatus == DownloadStageStatus.Succeeded ? DownloadJobState.Uploaded : DownloadJobState.Queued
            }
        };

    private static string BuildStopMessage(string? requestedBy, string? reason)
    {
        var who = string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim();
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return (who, why) switch
        {
            ({ } user, { } text) => $"Download stopped by {user}: {text}",
            ({ } user, null) => $"Download stopped by {user}.",
            (null, { } text) => $"Download stopped: {text}",
            _ => "Download stopped by request."
        };
    }
}
