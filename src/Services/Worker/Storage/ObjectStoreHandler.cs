using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Handles file transfer via NATS Object Store.
/// Uploads file to NATS Object Store, DataBridge downloads from there.
/// </summary>
public class ObjectStoreHandler : IStorageHandler
{
    private readonly ILogger<ObjectStoreHandler> _logger;

    public ObjectStoreHandler(ILogger<ObjectStoreHandler> logger)
    {
        _logger = logger;
    }

    public StorageMethod SupportedMethod => StorageMethod.ObjectStore;

    public Task HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct)
    {
        _logger.LogWarning("Job {JobId}: ObjectStore method not yet implemented", job.JobId);

        // TODO: Implement NATS Object Store upload
        // 1. Get IObjectStore from DI (inject in constructor)
        // 2. Open source file stream
        // 3. Upload to Object Store with job ID as key: await objectStore.PutAsync(job.JobId, stream, ct)
        // 4. Publish event to notify DataBridge of the object store key
        // 5. DataBridge downloads from Object Store and deletes after successful transfer

        if (string.IsNullOrEmpty(config.ObjectStoreBucket))
        {
            _logger.LogError("Job {JobId}: ObjectStore requires a bucket name", job.JobId);
        }

        throw new NotImplementedException(
            $"ObjectStore storage method is not yet implemented for job {job.JobId}");
    }
}
