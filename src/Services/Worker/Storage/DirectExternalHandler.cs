using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Handles file transfer via direct upload to external storage (S3, Azure Blob, etc.).
/// Worker uploads directly to external storage, DataBridge receives metadata only.
/// </summary>
public class DirectExternalHandler : IStorageHandler
{
    private readonly ILogger<DirectExternalHandler> _logger;

    public DirectExternalHandler(ILogger<DirectExternalHandler> logger)
    {
        _logger = logger;
    }

    public StorageMethod SupportedMethod => StorageMethod.DirectExternal;

    public Task HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct)
    {
        _logger.LogWarning("Job {JobId}: DirectExternal method not yet implemented", job.JobId);

        // TODO: Implement direct S3/external storage upload
        // 1. Get presigned URL or credentials from config.ExternalEndpoint
        // 2. Open source file stream
        // 3. Upload to S3 using multipart upload for large files
        // 4. Calculate checksum during upload
        // 5. Publish event to notify DataBridge of the S3 key/URL

        if (string.IsNullOrEmpty(config.ExternalEndpoint))
        {
            _logger.LogError("Job {JobId}: DirectExternal requires an external endpoint", job.JobId);
        }

        throw new NotImplementedException(
            $"DirectExternal storage method is not yet implemented for job {job.JobId}");
    }
}
