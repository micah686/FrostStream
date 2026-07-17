using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Text.Json;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class DownloadJobsRepository(
    DataBridgeDbContext db,
    IClock clock,
    IDownloadJobStateNotifier? stateNotifier = null) : IDownloadJobsRepository
{
    private const int DefaultQueueLimit = 50;
    private const int MaxQueueLimit = 200;

    /// <summary>
    /// States a job must never leave via event-derived or flow-derived progress writes.
    /// The Cleipnir flow and <see cref="Messaging.DownloadEventsConsumerService"/> commit on
    /// independent connections, so a stale progress state (e.g. Uploaded) can attempt to land
    /// after the flow already committed a terminal state; these writes must lose.
    /// </summary>
    private static readonly DownloadJobState[] TerminalStates =
    [
        DownloadJobState.Completed,
        DownloadJobState.AlreadyDownloaded,
        DownloadJobState.FailedPermanent,
        DownloadJobState.DeadLettered,
        DownloadJobState.Cancelled,
        DownloadJobState.Ignored
    ];

    private readonly IDownloadJobStateNotifier _stateNotifier = stateNotifier ?? NullDownloadJobStateNotifier.Instance;

    /// <summary>Fires a live state-changed notification when a transition actually occurred. Best-effort.</summary>
    private Task NotifyStateAsync(DownloadJobEntity job, DownloadJobState previousState, CancellationToken ct)
        => previousState == job.State
            ? Task.CompletedTask
            : _stateNotifier.NotifyAsync(job.JobId, job.State, previousState, job.CorrelationId, ct);

    public Task<bool> IsMessageProcessedAsync(Guid messageId, CancellationToken ct = default)
        => db.ProcessedMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId, ct);

    public async Task<bool> TryMarkMessageProcessedAsync(Guid messageId, string operationKey, Guid jobId, CancellationToken ct = default)
    {
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO downloads.processed_messages (message_id, operation_key, job_id)
             VALUES ({messageId}, {operationKey}, {jobId})
             ON CONFLICT (message_id) DO NOTHING
             """,
            ct);

        return inserted == 1;
    }

    public async Task MarkMessageProcessedAsync(Guid messageId, string operationKey, Guid jobId, CancellationToken ct = default)
        => await TryMarkMessageProcessedAsync(messageId, operationKey, jobId, ct);

    public async Task CreateJobIfMissingAsync(DownloadRequested request, CancellationToken ct = default)
    {
        var exists = await db.DownloadJobs.AnyAsync(x => x.JobId == request.JobId, ct);
        if (exists)
            return;

        db.DownloadJobs.Add(new DownloadJobEntity
        {
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            State = DownloadJobState.Queued,
            SourceUrl = request.SourceUrl,
            RequestedBy = request.RequestedBy,
            StorageKey = request.StorageKey,
            SourceKind = request.SourceKind,
            Priority = request.Priority,
            IngestOrigin = IngestOrigin.Download
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<DownloadRequested?> GetOriginalRequestAsync(Guid jobId, CancellationToken ct = default)
    {
        var row = await db.DownloadJobHistory
            .AsNoTracking()
            .Where(x => x.JobId == jobId && x.EventName == nameof(DownloadRequested))
            .OrderBy(x => x.RecordedAt)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(row)
            ? null
            : JsonSerializer.Deserialize<DownloadRequested>(row);
    }

    public async Task<MetadataFetched?> GetLastMetadataFetchedAsync(Guid jobId, CancellationToken ct = default)
    {
        var row = await db.DownloadJobHistory
            .AsNoTracking()
            .Where(x => x.JobId == jobId && x.EventName == nameof(MetadataFetched))
            .OrderByDescending(x => x.RecordedAt)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(row)
            ? null
            : JsonSerializer.Deserialize<MetadataFetched>(row);
    }

    public async Task UpdateStateAsync(Guid jobId, DownloadJobState state, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        if (job.State is DownloadJobState.Cancelled && state is DownloadJobState.Queued)
        {
            job.FailureKind = null;
            job.FailureCode = null;
            job.FailureMessage = null;
            job.CompletedAt = null;
        }
        else if (job.State is DownloadJobState.ProviderHalted && state is DownloadJobState.Queued)
        {
            job.FailureKind = null;
            job.FailureCode = null;
            job.FailureMessage = null;
        }
        else if ((job.State is DownloadJobState.Cancelling or DownloadJobState.Cancelled) && state != DownloadJobState.Cancelled)
        {
            return;
        }
        else if (TerminalStates.Contains(job.State))
        {
            return;
        }

        var previousState = job.State;
        job.State = state;
        job.UpdatedAt = clock.GetCurrentInstant();
        if (state is DownloadJobState.Completed or DownloadJobState.AlreadyDownloaded)
            job.CompletedAt = job.UpdatedAt;
        await db.SaveChangesAsync(ct);
        await NotifyStateAsync(job, previousState, ct);
    }

    public async Task ApplyMetadataAsync(Guid jobId, MetadataFetched evt, CancellationToken ct = default)
    {
        var snapshot = await GetStateSnapshotAsync(jobId, ct);
        var now = clock.GetCurrentInstant();
        var affected = await db.DownloadJobs
            .Where(ProgressWritable(jobId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, DownloadJobState.MetadataResolved)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await NotifyProgressAsync(snapshot, affected, DownloadJobState.MetadataResolved, ct);
    }

    /// <summary>
    /// Predicate for event-derived progress writes. Evaluated inside the UPDATE so that a
    /// terminal state committed concurrently by the flow wins even when this repository's
    /// transaction commits last (the UPDATE re-evaluates its predicate after the blocking
    /// writer commits; an entity-load check would not).
    /// </summary>
    private static Expression<Func<DownloadJobEntity, bool>> ProgressWritable(Guid jobId)
        => x => x.JobId == jobId
                && !TerminalStates.Contains(x.State)
                && x.State != DownloadJobState.Cancelling;

    private async Task<(Guid JobId, DownloadJobState State, Guid CorrelationId)> GetStateSnapshotAsync(Guid jobId, CancellationToken ct)
    {
        var row = await db.DownloadJobs
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => new { x.State, x.CorrelationId })
            .FirstAsync(ct);
        return (jobId, row.State, row.CorrelationId);
    }

    private Task NotifyProgressAsync((Guid JobId, DownloadJobState State, Guid CorrelationId) snapshot, int affected, DownloadJobState newState, CancellationToken ct)
        => affected == 0 || snapshot.State == newState
            ? Task.CompletedTask
            : _stateNotifier.NotifyAsync(snapshot.JobId, newState, snapshot.State, snapshot.CorrelationId, ct);

    public async Task<SourceVersionDecision> CheckSourceVersionAsync(MetadataFetched evt, bool forceDownload, CancellationToken ct = default)
    {
        var provider = NormalizeOptional(evt.Provider);
        var sourceMediaId = NormalizeOptional(evt.SourceMediaId);
        if (provider is null || sourceMediaId is null)
            return new SourceVersionDecision(false, null, null);

        var source = await db.MediaSourceVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Provider == provider && x.SourceMediaId == sourceMediaId, ct);

        if (source is null)
            return new SourceVersionDecision(false, null, null);

        var sameLastModified = source.SourceLastModified == evt.SourceLastModified;
        var alreadyDownloaded = !forceDownload && sameLastModified;
        return new SourceVersionDecision(alreadyDownloaded, source.MediaGuid, source.LatestJobId);
    }

    public async Task MarkAlreadyDownloadedAsync(Guid jobId, Guid mediaGuid, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        var latest = await db.MediaContentIdVersions
            .AsNoTracking()
            .Where(x => x.MediaGuid == mediaGuid)
            .OrderByDescending(x => x.VersionNum)
            .FirstOrDefaultAsync(ct);

        var previousState = job.State;
        job.State = DownloadJobState.AlreadyDownloaded;
        if (latest is not null)
        {
            job.StorageKey = latest.StorageKey;
            job.ContentHashXxh128 = latest.ContentHashXxh128;
        }
        job.UpdatedAt = clock.GetCurrentInstant();
        job.CompletedAt = job.UpdatedAt;
        await db.SaveChangesAsync(ct);
        await NotifyStateAsync(job, previousState, ct);
    }

    public async Task DeleteReservedVersionAsync(Guid mediaGuid, int versionNum, CancellationToken ct = default)
    {
        var row = await db.MediaContentIdVersions
            .FirstOrDefaultAsync(x => x.MediaGuid == mediaGuid && x.VersionNum == versionNum, ct);
        if (row is null)
            return;
        db.MediaContentIdVersions.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplyDownloadCompletedAsync(Guid jobId, DownloadCompleted evt, CancellationToken ct = default)
    {
        var snapshot = await GetStateSnapshotAsync(jobId, ct);
        var now = clock.GetCurrentInstant();
        var contentHash = NormalizeHash(evt.ContentHashXxh128);
        var affected = await db.DownloadJobs
            .Where(ProgressWritable(jobId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.TempFileRef, evt.TempFileRef)
                .SetProperty(x => x.FileSizeBytes, evt.FileSizeBytes)
                .SetProperty(x => x.ContentHashXxh128, contentHash)
                .SetProperty(x => x.State, DownloadJobState.DownloadedTemp)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await NotifyProgressAsync(snapshot, affected, DownloadJobState.DownloadedTemp, ct);
    }

    public async Task<VersionReservation> ReserveVersionAsync(VersionReservationRequest request, CancellationToken ct = default)
    {
        var contentHash = NormalizeHash(request.ContentHashXxh128)
            ?? throw new ArgumentException("Content hash is required.", nameof(request));
        var storageKey = request.StorageKey;
        var provider = NormalizeOptional(request.Provider);
        var sourceMediaId = NormalizeOptional(request.SourceMediaId);

        // Option (a): if these exact bytes already exist on this storage, reuse the
        // existing media_guid + storage_path. No new content row, no upload needed.
        var existingContent = await db.MediaContentIdVersions
            .FirstOrDefaultAsync(x => x.StorageKey == storageKey && x.ContentHashXxh128 == contentHash, ct);

        Guid mediaGuid;
        string storagePath;
        int versionNum;
        bool contentAlreadyStored;
        bool isNewMediaGuid;

        if (existingContent is not null)
        {
            mediaGuid = existingContent.MediaGuid;
            storagePath = existingContent.StoragePath;
            versionNum = existingContent.VersionNum;
            contentAlreadyStored = true;
            isNewMediaGuid = false;
        }
        else
        {
            // No prior bytes: fall back to source row's media_guid (if any), else mint a new one.
            var existingSource = (provider is not null && sourceMediaId is not null)
                ? await db.MediaSourceVersions
                    .FirstOrDefaultAsync(x => x.Provider == provider && x.SourceMediaId == sourceMediaId, ct)
                : null;

            isNewMediaGuid = existingSource is null;
            mediaGuid = existingSource?.MediaGuid ?? Guid.NewGuid();

            var maxVersion = await db.MediaContentIdVersions
                .Where(x => x.MediaGuid == mediaGuid)
                .Select(x => (int?)x.VersionNum)
                .MaxAsync(ct) ?? 0;
            versionNum = maxVersion + 1;

            storagePath = BuildStoragePath(mediaGuid, versionNum, request.FileName);

            db.MediaContentIdVersions.Add(new MediaContentIdVersionEntity
            {
                MediaGuid = mediaGuid,
                ContentHashXxh128 = contentHash,
                StorageKey = storageKey,
                StoragePath = storagePath,
                VersionNum = versionNum,
                IngestOrigin = request.IngestOrigin
            });
            contentAlreadyStored = false;
        }

        // Upsert the source row pointing at media_guid. If it already exists with a different
        // media_guid, prefer the bytes-derived one (option a) — same bytes win as identity.
        if (provider is not null && sourceMediaId is not null)
        {
            var sourceRow = await db.MediaSourceVersions
                .FirstOrDefaultAsync(x => x.Provider == provider && x.SourceMediaId == sourceMediaId, ct);
            if (sourceRow is null)
            {
                db.MediaSourceVersions.Add(new MediaSourceVersionEntity
                {
                    Provider = provider,
                    SourceMediaId = sourceMediaId,
                    SourceLastModified = request.SourceLastModified,
                    MediaGuid = mediaGuid,
                    LatestJobId = request.LinkSourceToDownloadJob ? request.JobId : null
                });
            }
            else
            {
                sourceRow.SourceLastModified = request.SourceLastModified ?? sourceRow.SourceLastModified;
                sourceRow.MediaGuid = mediaGuid;
                if (request.LinkSourceToDownloadJob)
                    sourceRow.LatestJobId = request.JobId;
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO media.media (media_guid) VALUES ({mediaGuid}) ON CONFLICT (media_guid) DO NOTHING", ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return new VersionReservation(mediaGuid, storagePath, versionNum, contentAlreadyStored, isNewMediaGuid);
    }

    public async Task DeleteNewMediaGuidAsync(Guid mediaGuid, string? provider, string? sourceMediaId, CancellationToken ct = default)
    {
        var normalizedProvider = NormalizeOptional(provider);
        var normalizedSourceId = NormalizeOptional(sourceMediaId);

        if (normalizedProvider is not null && normalizedSourceId is not null)
        {
            var sourceRow = await db.MediaSourceVersions
                .FirstOrDefaultAsync(x => x.Provider == normalizedProvider && x.SourceMediaId == normalizedSourceId, ct);
            if (sourceRow is not null)
                db.MediaSourceVersions.Remove(sourceRow);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM media.media WHERE media_guid = {mediaGuid}", ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task CommitUploadAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default)
    {
        var snapshot = await GetStateSnapshotAsync(jobId, ct);
        var now = clock.GetCurrentInstant();
        var contentHash = NormalizeHash(evt.ContentHashXxh128);
        var contentLength = evt.ContentLengthBytes;
        var affected = await db.DownloadJobs
            .Where(ProgressWritable(jobId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.StorageKey, evt.StorageKey)
                .SetProperty(x => x.StorageVersion, evt.StorageVersion)
                .SetProperty(x => x.ContentHashXxh128, x => contentHash ?? x.ContentHashXxh128)
                .SetProperty(x => x.FileSizeBytes, x => contentLength ?? x.FileSizeBytes)
                .SetProperty(x => x.State, DownloadJobState.Uploaded)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await NotifyProgressAsync(snapshot, affected, DownloadJobState.Uploaded, ct);
    }

    public async Task ApplySidecarUploadCompletedAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.InfoJsonStoragePath = evt.StoragePath;
        job.InfoJsonContentHashXxh128 = NormalizeHash(evt.ContentHashXxh128);
        if (evt.ContentLengthBytes is { } len)
            job.InfoJsonSizeBytes = len;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplyMetaUploadCompletedAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.MetaStoragePath = evt.StoragePath;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdatePriorityAsync(Guid jobId, int priority, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (job is null) return false;
        job.Priority = priority;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(DownloadJobState? State, string? StorageKey)> GetJobStateAndStorageKeyAsync(Guid jobId, CancellationToken ct = default)
    {
        var row = await db.DownloadJobs
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => new { x.State, x.StorageKey })
            .FirstOrDefaultAsync(ct);
        return row is null ? (null, null) : (row.State, row.StorageKey);
    }

    public async Task<CancelDownloadDecision> TryBeginCancellationAsync(
        Guid jobId,
        string? requestedBy,
        string? reason,
        CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstOrDefaultAsync(x => x.JobId == jobId, ct);

        if (job is null)
            return CancelDownloadDecision.NotFound();

        var workerTag = await db.StorageConfigs
            .Where(x => x.Key == job.StorageKey)
            .Select(x => x.WorkerTag)
            .FirstOrDefaultAsync(ct);

        if (job.State is DownloadJobState.Cancelled)
            return CancelDownloadDecision.Rejected(job.State, "Job is already cancelled.", alreadyTerminal: true);

        if (job.State is DownloadJobState.Completed or DownloadJobState.AlreadyDownloaded)
            return CancelDownloadDecision.Rejected(job.State, "Job is already complete.", alreadyTerminal: true);

        if (job.State is DownloadJobState.FailedTransient)
        {
            // The saga already ran its terminal Fail() step for this job, so there's no active
            // Cleipnir flow instance left to drive the normal Cancelling -> Cancelled transition.
            // Transition straight to Cancelled and let the caller still notify the Worker — a known
            // race (JetStream redelivering DownloadVideoCommand mid-retry-backoff, e.g. during a
            // rate-limited sidecar/subtitle fetch) can leave a yt-dlp process running for this JobId
            // even though the DB already recorded FailedTransient.
            var failedPreviousState = job.State;
            job.State = DownloadJobState.Cancelled;
            job.FailureKind = FailureKind.Cancelled;
            job.FailureCode = "cancel_requested";
            job.FailureMessage = BuildCancellationMessage(requestedBy, reason);
            job.UpdatedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            await NotifyStateAsync(job, failedPreviousState, ct);
            return CancelDownloadDecision.AcceptedFor(job.CorrelationId, job.State, failedPreviousState, workerTag);
        }

        if (job.State is DownloadJobState.FailedPermanent or DownloadJobState.DeadLettered or DownloadJobState.ProviderHalted)
            return CancelDownloadDecision.Rejected(job.State, "Job has already failed.", alreadyTerminal: true);

        if (job.State is DownloadJobState.UploadPending or DownloadJobState.Uploaded or DownloadJobState.CommitPending or DownloadJobState.Compensating or DownloadJobState.DownloadedTemp)
        {
            return CancelDownloadDecision.Rejected(
                job.State,
                $"Job cannot be cancelled cleanly from state {job.State}.");
        }

        if (job.State is not (DownloadJobState.Queued or DownloadJobState.MetadataPending or DownloadJobState.MetadataResolved or DownloadJobState.DownloadQueued or DownloadJobState.DownloadPending or DownloadJobState.Cancelling))
        {
            return CancelDownloadDecision.Rejected(
                job.State,
                $"Job cannot be cancelled from state {job.State}.");
        }

        var previousState = job.State;
        if (job.State != DownloadJobState.Cancelling)
        {
            job.State = DownloadJobState.Cancelling;
            job.FailureKind = FailureKind.Cancelled;
            job.FailureCode = "cancel_requested";
            job.FailureMessage = BuildCancellationMessage(requestedBy, reason);
            job.UpdatedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            await NotifyStateAsync(job, previousState, ct);
        }

        return CancelDownloadDecision.AcceptedFor(job.CorrelationId, job.State, previousState, workerTag);
    }

    public async Task MarkCancelledAsync(Guid jobId, string? message, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        var previousState = job.State;
        job.State = DownloadJobState.Cancelled;
        job.FailureKind = FailureKind.Cancelled;
        job.FailureCode = "cancel_requested";
        job.FailureMessage = string.IsNullOrWhiteSpace(message) ? "Download cancelled by request." : message;
        job.UpdatedAt = clock.GetCurrentInstant();
        job.CompletedAt = job.UpdatedAt;

        var alreadyTerminal = await db.FailedDownloadJobs.AnyAsync(x => x.JobId == jobId, ct);
        if (!alreadyTerminal)
        {
            db.FailedDownloadJobs.Add(new FailedDownloadJobEntity
            {
                JobId = jobId,
                CorrelationId = job.CorrelationId,
                FailedState = DownloadJobState.Cancelled,
                FailureKind = FailureKind.Cancelled,
                FailureCode = job.FailureCode,
                FailureMessage = job.FailureMessage,
                LastPayloadJson = null
            });
        }

        await db.SaveChangesAsync(ct);
        await NotifyStateAsync(job, previousState, ct);
    }

    public async Task<IReadOnlyList<DownloadQueuedEntry>> GetDownloadQueuedJobsAsync(CancellationToken ct = default)
    {
        var rows = await db.DownloadJobs
            .AsNoTracking()
            .Where(x => x.State == DownloadJobState.DownloadQueued)
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.CreatedAt)
            .Select(x => new { x.JobId, x.Priority, x.CreatedAt, x.StorageKey })
            .ToListAsync(ct);
        return rows.Select(r => new DownloadQueuedEntry(r.JobId, r.Priority, r.CreatedAt, r.StorageKey)).ToList();
    }

    public async Task IncrementMetadataAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.AttemptMetadata = attempt;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task IncrementDownloadAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.AttemptDownload = attempt;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task IncrementUploadAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.AttemptUpload = attempt;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordHistoryAsync(Guid jobId, Guid messageId, string operationKey, string eventName, string? payloadJson, CancellationToken ct = default)
    {
        db.DownloadJobHistory.Add(new DownloadJobHistoryEntity
        {
            JobId = jobId,
            MessageId = messageId,
            OperationKey = operationKey,
            EventName = eventName,
            PayloadJson = payloadJson
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task AppendProgressLogAsync(Guid jobId, int sequence, string message, CancellationToken ct = default)
    {
        db.DownloadJobProgressLog.Add(new DownloadJobProgressLogEntity
        {
            JobId = jobId,
            Sequence = sequence,
            Message = message.Length > 2048 ? message[..2048] : message
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Advisory telemetry, not saga state — safe to drop if the job no longer exists (FK
            // violation) or a write races with the job being deleted.
            db.ChangeTracker.Clear();
        }
    }

    public async Task RecordTerminalFailureAsync(Guid jobId, FailureKind kind, string? code, string message, DownloadJobState terminalState, string? lastPayloadJson, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        var previousState = job.State;
        job.State = terminalState;
        job.FailureKind = kind;
        job.FailureCode = code;
        job.FailureMessage = message;
        job.UpdatedAt = clock.GetCurrentInstant();

        var alreadyTerminal = await db.FailedDownloadJobs.AnyAsync(x => x.JobId == jobId, ct);
        if (!alreadyTerminal)
        {
            db.FailedDownloadJobs.Add(new FailedDownloadJobEntity
            {
                JobId = jobId,
                CorrelationId = job.CorrelationId,
                FailedState = terminalState,
                FailureKind = kind,
                FailureCode = code,
                FailureMessage = message,
                LastPayloadJson = lastPayloadJson
            });
        }

        await db.SaveChangesAsync(ct);
        await NotifyStateAsync(job, previousState, ct);
    }

    public async Task<DownloadQueuePage> QueryQueueAsync(DownloadQueueListRequest request, CancellationToken ct = default)
    {
        var limit = Math.Clamp(request.Limit <= 0 ? DefaultQueueLimit : request.Limit, 1, MaxQueueLimit);
        var offset = DecodeCursor(request.Cursor);

        var query = db.DownloadJobs.AsNoTracking().AsQueryable();

        if (request.State is { } state)
        {
            query = query.Where(x => x.State == state);
        }
        else
        {
            query = request.StateGroup switch
            {
                DownloadQueueStateGroup.Active => query.Where(x =>
                    x.State == DownloadJobState.MetadataPending
                    || x.State == DownloadJobState.MetadataResolved
                    || x.State == DownloadJobState.DownloadPending
                    || x.State == DownloadJobState.UploadPending
                    || x.State == DownloadJobState.CommitPending
                    || x.State == DownloadJobState.Compensating
                    || x.State == DownloadJobState.Cancelling),
                DownloadQueueStateGroup.Queued => query.Where(x =>
                    x.State == DownloadJobState.Queued
                    || x.State == DownloadJobState.DownloadQueued),
                DownloadQueueStateGroup.Failed => query.Where(x =>
                    x.State == DownloadJobState.FailedTransient
                    || x.State == DownloadJobState.FailedPermanent
                    || x.State == DownloadJobState.DeadLettered
                    || x.State == DownloadJobState.ProviderHalted),
                DownloadQueueStateGroup.Done => query.Where(x =>
                    x.State == DownloadJobState.Completed
                    || x.State == DownloadJobState.AlreadyDownloaded),
                DownloadQueueStateGroup.Cancelled => query.Where(x =>
                    x.State == DownloadJobState.Cancelled
                    || x.State == DownloadJobState.Ignored),
                _ => query
            };
        }
        if (request.SourceKind is { } sourceKind)
            query = query.Where(x => x.SourceKind == sourceKind);

        var requestedBy = NormalizeOptional(request.RequestedBy);
        if (requestedBy is not null)
            query = query.Where(x => x.RequestedBy == requestedBy);

        var storageKey = NormalizeOptional(request.StorageKey);
        if (storageKey is not null)
            query = query.Where(x => x.StorageKey == storageKey);

        if (request.CreatedFrom is { } from)
            query = query.Where(x => x.CreatedAt >= from);
        if (request.CreatedTo is { } to)
            query = query.Where(x => x.CreatedAt <= to);

        var search = NormalizeOptional(request.Query);
        if (search is not null)
        {
            var pattern = $"%{EscapeLike(search)}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.SourceUrl, pattern, "\\")
                || EF.Functions.ILike(x.JobId.ToString(), pattern, "\\")
                || (x.RequestedBy != null && EF.Functions.ILike(x.RequestedBy, pattern, "\\"))
                || (x.StorageKey != null && EF.Functions.ILike(x.StorageKey, pattern, "\\"))
                || (x.FailureCode != null && EF.Functions.ILike(x.FailureCode, pattern, "\\"))
                || (x.FailureMessage != null && EF.Functions.ILike(x.FailureMessage, pattern, "\\")));
        }

        var totalCount = await query.CountAsync(ct);

        // Total, deterministic ordering (job_id is the unique tiebreak) so offset paging is stable.
        var ordered = request.Sort == DownloadQueueSort.Priority
            ? query.OrderByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.JobId)
            : query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.JobId);

        var rows = await ordered
            .Skip(offset)
            .Take(limit)
            .Select(QueueDtoProjection)
            .ToListAsync(ct);

        var nextOffset = offset + rows.Count;
        var nextCursor = nextOffset < totalCount ? EncodeCursor(nextOffset) : null;

        return new DownloadQueuePage(rows, nextCursor, totalCount);
    }

    public async Task<DownloadQueueJobDto?> GetQueueJobAsync(Guid jobId, CancellationToken ct = default)
    {
        return await db.DownloadJobs
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(QueueDtoProjection)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// <see cref="DownloadQueueHistoryEntryDto.EventName"/> marker for a merged-in progress-log row.
    /// The frontend renders these as the raw yt-dlp line (<see cref="DownloadQueueHistoryEntryDto.PayloadJson"/>)
    /// instead of "[time] EventName".
    /// </summary>
    private const string ProgressLineEventName = "ProgressLine";

    public async Task<IReadOnlyList<DownloadQueueHistoryEntryDto>?> GetQueueHistoryAsync(Guid jobId, CancellationToken ct = default)
    {
        var exists = await db.DownloadJobs.AsNoTracking().AnyAsync(x => x.JobId == jobId, ct);
        if (!exists)
            return null;

        var historyEntries = await db.DownloadJobHistory
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => new DownloadQueueHistoryEntryDto
            {
                Id = x.Id,
                MessageId = x.MessageId,
                OperationKey = x.OperationKey,
                EventName = x.EventName,
                PayloadJson = x.PayloadJson,
                RecordedAt = x.RecordedAt
            })
            .ToListAsync(ct);

        // Progress-log ids are negated so they can never collide with history ids (both are
        // independent bigserial sequences) when the two lists are merged for the client.
        // Projected as an anonymous type first (real columns only) and mapped to the DTO afterwards,
        // in-memory — projecting the Guid.Empty/"progress"/ProgressLineEventName constants directly
        // into the IQueryable made Npgsql infer a "text" column for MessageId, which then failed to
        // read back as a Guid at materialization time.
        var progressLogRows = await db.DownloadJobProgressLog
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => new { x.Id, x.Message, x.RecordedAt })
            .ToListAsync(ct);

        var progressLines = progressLogRows
            .Select(x => new DownloadQueueHistoryEntryDto
            {
                Id = -x.Id,
                MessageId = Guid.Empty,
                OperationKey = "progress",
                EventName = ProgressLineEventName,
                PayloadJson = x.Message,
                RecordedAt = x.RecordedAt
            });

        return historyEntries
            .Concat(progressLines)
            .OrderBy(x => x.RecordedAt)
            .ThenBy(x => x.Id)
            .ToList();
    }

    public async Task<Guid?> GetMediaGuidForJobAsync(Guid jobId, CancellationToken ct = default)
    {
        return await db.MediaSourceVersions
            .AsNoTracking()
            .Where(x => x.LatestJobId == jobId)
            .Select(x => (Guid?)x.MediaGuid)
            .FirstOrDefaultAsync(ct);
    }

    // Shared projection (as an expression tree so EF can translate it) — list and detail
    // return identical snapshots.
    private static readonly Expression<Func<DownloadJobEntity, DownloadQueueJobDto>> QueueDtoProjection = x => new DownloadQueueJobDto
    {
        JobId = x.JobId,
        CorrelationId = x.CorrelationId,
        State = x.State,
        SourceUrl = x.SourceUrl,
        RequestedBy = x.RequestedBy,
        StorageKey = x.StorageKey,
        SourceKind = x.SourceKind,
        Priority = x.Priority,
        AttemptMetadata = x.AttemptMetadata,
        AttemptDownload = x.AttemptDownload,
        AttemptUpload = x.AttemptUpload,
        FileSizeBytes = x.FileSizeBytes,
        ContentHashXxh128 = x.ContentHashXxh128,
        FailureKind = x.FailureKind,
        FailureCode = x.FailureCode,
        FailureMessage = x.FailureMessage,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt,
        CompletedAt = x.CompletedAt
    };

    private static string EncodeCursor(int offset)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset >= 0
                ? offset
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string BuildStoragePath(Guid mediaGuid, int versionNum, string fileName)
    {
        var sanitized = SanitizeFileName(fileName);
        return $"archives/{mediaGuid:N}/v{versionNum}/{sanitized}";
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "video.bin";

        var name = Path.GetFileName(fileName);
        var chars = name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var safe = new string(chars).Trim('-', '.');
        if (safe.Length == 0)
            return "video.bin";
        return safe.Length <= 120 ? safe : safe[..120];
    }

    private static string? NormalizeHash(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string BuildCancellationMessage(string? requestedBy, string? reason)
    {
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var trimmedRequester = string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim();

        return (trimmedRequester, trimmedReason) switch
        {
            ({ } requester, { } why) => $"Download cancelled by {requester}: {why}",
            ({ } requester, null) => $"Download cancelled by {requester}.",
            (null, { } why) => $"Download cancelled by request: {why}",
            _ => "Download cancelled by request."
        };
    }
}
