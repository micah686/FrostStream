using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Interface for handling file transfers using different storage methods.
/// Each implementation handles a specific <see cref="StorageMethod"/>.
/// </summary>
public interface IStorageHandler
{
    /// <summary>
    /// Gets the storage method this handler supports.
    /// </summary>
    StorageMethod SupportedMethod { get; }

    /// <summary>
    /// Handles file transfer for the specified job using this storage method.
    /// </summary>
    /// <param name="job">The job request containing job details.</param>
    /// <param name="config">Storage configuration from DataBridge.</param>
    /// <param name="workerId">The worker's unique identifier.</param>
    /// <param name="sourceVideoPath">Path to the source video file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct);
}
