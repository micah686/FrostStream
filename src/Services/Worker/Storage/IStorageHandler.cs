using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Result of a storage handler writing a file, including the computed hash and file size.
/// </summary>
public record StorageResult(string StagedPath, string XxHash, long FileSizeBytes);

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
    /// Returns a <see cref="StorageResult"/> with the staged path, hash, and file size.
    /// </summary>
    Task<StorageResult> HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct);
}
