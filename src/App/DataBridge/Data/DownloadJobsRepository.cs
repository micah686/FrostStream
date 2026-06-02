using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class DownloadJobsRepository(DataBridgeDbContext db, IClock clock) : IDownloadJobsRepository
{
    public Task<bool> IsMessageProcessedAsync(Guid messageId, CancellationToken ct = default)
        => db.ProcessedMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId, ct);

    public async Task MarkMessageProcessedAsync(Guid messageId, string operationKey, Guid jobId, CancellationToken ct = default)
    {
        var exists = await db.ProcessedMessages.AnyAsync(x => x.MessageId == messageId, ct);
        if (exists)
            return;

        db.ProcessedMessages.Add(new ProcessedMessageEntity
        {
            MessageId = messageId,
            OperationKey = operationKey,
            JobId = jobId
        });
        await db.SaveChangesAsync(ct);
    }

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
            StorageKey = request.StorageKey
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateStateAsync(Guid jobId, DownloadJobState state, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.State = state;
        job.UpdatedAt = clock.GetCurrentInstant();
        if (state is DownloadJobState.Completed or DownloadJobState.AlreadyDownloaded)
            job.CompletedAt = job.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplyMetadataAsync(Guid jobId, MetadataFetched evt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.State = DownloadJobState.MetadataResolved;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

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

        job.State = DownloadJobState.AlreadyDownloaded;
        if (latest is not null)
        {
            job.StorageKey = latest.StorageKey;
            job.ContentHashXxh128 = latest.ContentHashXxh128;
        }
        job.UpdatedAt = clock.GetCurrentInstant();
        job.CompletedAt = job.UpdatedAt;
        await db.SaveChangesAsync(ct);
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
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.TempFileRef = evt.TempFileRef;
        job.FileSizeBytes = evt.FileSizeBytes;
        job.ContentHashXxh128 = NormalizeHash(evt.ContentHashXxh128);
        job.State = DownloadJobState.DownloadedTemp;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
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
                VersionNum = versionNum
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
                    LatestJobId = request.JobId
                });
            }
            else
            {
                sourceRow.SourceLastModified = request.SourceLastModified ?? sourceRow.SourceLastModified;
                sourceRow.MediaGuid = mediaGuid;
                sourceRow.LatestJobId = request.JobId;
            }
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO media (media_guid) VALUES ({mediaGuid}) ON CONFLICT (media_guid) DO NOTHING", ct);

        await db.SaveChangesAsync(ct);

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

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM media WHERE media_guid = {mediaGuid}", ct);

        await db.SaveChangesAsync(ct);
    }

    public async Task CommitUploadAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.StorageKey = evt.StorageKey;
        job.StorageVersion = evt.StorageVersion;
        var contentHash = NormalizeHash(evt.ContentHashXxh128);
        if (contentHash is not null)
            job.ContentHashXxh128 = contentHash;
        if (evt.ContentLengthBytes is { } len)
            job.FileSizeBytes = len;
        job.State = DownloadJobState.Uploaded;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
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

    public async Task RecordTerminalFailureAsync(Guid jobId, FailureKind kind, string? code, string message, DownloadJobState terminalState, string? lastPayloadJson, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
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
    }

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
}
