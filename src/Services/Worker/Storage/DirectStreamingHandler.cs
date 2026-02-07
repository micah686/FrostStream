using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Handles file transfer via direct streaming (gRPC/chunked messages).
/// Streams file directly to DataBridge without intermediate storage.
/// </summary>
public class DirectStreamingHandler : IStorageHandler
{
    private readonly ILogger<DirectStreamingHandler> _logger;

    public DirectStreamingHandler(ILogger<DirectStreamingHandler> logger)
    {
        _logger = logger;
    }

    public StorageMethod SupportedMethod => StorageMethod.DirectStreaming;

    public Task HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct)
    {
        _logger.LogWarning("Job {JobId}: DirectStreaming method not yet implemented", job.JobId);

        // TODO: Implement gRPC/chunked streaming to DataBridge
        // 1. Open source file stream
        // 2. Establish gRPC bidirectional stream or chunked NATS messages
        // 3. Stream chunks with sequence numbers for resumability
        // 4. Receive acknowledgment from DataBridge on completion

        throw new NotImplementedException(
            $"DirectStreaming storage method is not yet implemented for job {job.JobId}");
    }
}
