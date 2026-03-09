using Shared.Messages;

namespace Shared.Storage;

/// <summary>
/// Client for coordinating job operations with the DataBridge service.
/// Abstracts the NATS subject names to decouple workers from DataBridge implementation details.
/// </summary>
public interface IJobCoordinationClient
{
    /// <summary>
    /// Starts a job and performs idempotency checks.
    /// </summary>
    Task<JobStartResponse> StartJobAsync(JobStartRequest request, CancellationToken ct);

    /// <summary>
    /// Reports job progress to the DataBridge.
    /// </summary>
    Task<JobProgressResponse> ReportProgressAsync(JobProgressRequest request, CancellationToken ct);

    /// <summary>
    /// Commits a video to the database after successful upload.
    /// </summary>
    Task<VideoCommitResponse> CommitVideoAsync(VideoCommitRequest request, CancellationToken ct);

    /// <summary>
    /// Reports job failure to the DataBridge.
    /// </summary>
    Task ReportFailureAsync(JobFailRequest request, CancellationToken ct);

    /// <summary>
    /// Gets the current status of a job.
    /// </summary>
    Task<JobStatusResponse?> GetStatusAsync(Guid jobId, CancellationToken ct);
}
