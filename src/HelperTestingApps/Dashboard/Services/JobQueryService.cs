using Dashboard.Models;
using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;

namespace Dashboard.Services;

public sealed class JobQueryService(DataBridgeDbContext db)
{
    public async Task<IReadOnlyList<JobSummary>> GetRecentJobsAsync(int take, CancellationToken ct = default)
        => await db.DownloadJobs
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAt)
            .Take(take)
            .Select(x => new JobSummary(
                x.JobId,
                x.CorrelationId,
                x.State,
                x.SourceUrl,
                x.RequestedBy,
                x.StorageKey,
                x.AttemptMetadata,
                x.AttemptDownload,
                x.AttemptUpload,
                x.FileSizeBytes,
                x.ContentHashXxh128,
                x.FailureMessage,
                x.CreatedAt.ToDateTimeOffset(),
                x.UpdatedAt.ToDateTimeOffset(),
                x.CompletedAt == null ? null : x.CompletedAt.Value.ToDateTimeOffset()))
            .ToListAsync(ct);

    public async Task<JobSummary?> GetJobAsync(Guid jobId, CancellationToken ct = default)
        => await db.DownloadJobs
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => new JobSummary(
                x.JobId,
                x.CorrelationId,
                x.State,
                x.SourceUrl,
                x.RequestedBy,
                x.StorageKey,
                x.AttemptMetadata,
                x.AttemptDownload,
                x.AttemptUpload,
                x.FileSizeBytes,
                x.ContentHashXxh128,
                x.FailureMessage,
                x.CreatedAt.ToDateTimeOffset(),
                x.UpdatedAt.ToDateTimeOffset(),
                x.CompletedAt == null ? null : x.CompletedAt.Value.ToDateTimeOffset()))
            .FirstOrDefaultAsync(ct);
}

