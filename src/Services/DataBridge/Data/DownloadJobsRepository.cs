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
        if (state == DownloadJobState.Completed)
            job.CompletedAt = job.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplyMetadataAsync(Guid jobId, MetadataFetched evt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.ArchiveKey = evt.ArchiveKey;
        job.State = DownloadJobState.MetadataResolved;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplyDownloadCompletedAsync(Guid jobId, DownloadCompleted evt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.TempFileRef = evt.TempFileRef;
        job.FileName = evt.FileName;
        job.FileSizeBytes = evt.FileSizeBytes;
        job.ContentHashXxh128 = evt.ContentHashXxh128;
        job.ContentType = evt.ContentType;
        job.State = DownloadJobState.DownloadedTemp;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task CommitUploadAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default)
    {
        var job = await db.DownloadJobs.FirstAsync(x => x.JobId == jobId, ct);
        job.ObjectKey = evt.ObjectKey;
        job.StorageVersion = evt.StorageVersion;
        if (!string.IsNullOrEmpty(evt.ContentHashXxh128))
            job.ContentHashXxh128 = evt.ContentHashXxh128;
        if (evt.ContentLengthBytes is { } len)
            job.FileSizeBytes = len;
        job.State = DownloadJobState.Uploaded;
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
}
