using System.Security.Cryptography;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Handles file transfer via local staging (shared filesystem).
/// Worker stages file locally, DataBridge picks it up from the shared path.
/// </summary>
public class LocalStagingHandler : IStorageHandler
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<LocalStagingHandler> _logger;

    public LocalStagingHandler(IMessageBus messageBus, ILogger<LocalStagingHandler> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    public StorageMethod SupportedMethod => StorageMethod.LocalStaging;

    public async Task HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.StagingPath))
        {
            _logger.LogError("Job {JobId}: LocalStaging requires a staging path", job.JobId);
            return;
        }

        var tempPath = Path.Combine(config.StagingPath, $"{job.JobId}.part");
        var finalStagingPath = Path.Combine(config.StagingPath, $"{job.JobId}.ready");

        _logger.LogInformation("Job {JobId}: Staging file to {TempPath}", job.JobId, tempPath);

        // Ensure staging directory exists
        Directory.CreateDirectory(config.StagingPath);

        // Copy file to staging area (in real scenario, this would be downloading/processing)
        // Here we copy the local video.mp4 to simulate the worker producing output
        if (!File.Exists(sourceVideoPath))
        {
            _logger.LogError("Job {JobId}: Source video file not found at {Path}", job.JobId, sourceVideoPath);
            return;
        }

        await using (var sourceStream = File.OpenRead(sourceVideoPath))
        await using (var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await sourceStream.CopyToAsync(destStream, ct);
        }

        // Calculate checksum
        var checksum = await CalculateChecksumAsync(tempPath, ct);
        var fileSize = new FileInfo(tempPath).Length;

        // Atomic rename signals completion (prevents DataBridge from reading incomplete file)
        File.Move(tempPath, finalStagingPath);

        _logger.LogInformation("Job {JobId}: File staged successfully at {Path}, checksum: {Checksum}",
            job.JobId, finalStagingPath, checksum);

        // Signal DataBridge that file is ready
        await _messageBus.PublishAsync(Subjects.FileStaged, new FileStagedEvent
        {
            JobId = job.JobId,
            LocalPath = finalStagingPath,
            FinalDestination = job.DestinationPath,
            Checksum = checksum,
            FileSizeBytes = fileSize,
            WorkerId = workerId
        }, ct);

        _logger.LogInformation("Job {JobId}: Published FileStagedEvent to DataBridge", job.JobId);
    }

    private static async Task<string> CalculateChecksumAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
