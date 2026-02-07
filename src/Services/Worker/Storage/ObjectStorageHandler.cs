using System.Security.Cryptography;
using FluentStorage;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Handles file transfer via object storage (S3, Azure Blob, GCS, MinIO, etc.).
/// Uses FluentStorage's AWS/Azure/GCP providers via connection string.
/// Requires FluentStorage.AWS, FluentStorage.Azure.Blobs, and FluentStorage.GCP modules to be registered at startup.
/// </summary>
public class ObjectStorageHandler : IStorageHandler
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<ObjectStorageHandler> _logger;

    public ObjectStorageHandler(IMessageBus messageBus, ILogger<ObjectStorageHandler> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    public StorageMethod SupportedMethod => StorageMethod.ObjectStorage;

    public async Task HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            _logger.LogError("Job {JobId}: ObjectStorage requires a connection string", job.JobId);
            return;
        }

        if (!File.Exists(sourceVideoPath))
        {
            _logger.LogError("Job {JobId}: Source video file not found at {Path}", job.JobId, sourceVideoPath);
            return;
        }

        using var storage = StorageFactory.Blobs.FromConnectionString(config.ConnectionString);

        var remotePath = config.RemotePath ?? "";
        var objectKey = $"{job.JobId}{Path.GetExtension(sourceVideoPath)}";
        var fullPath = string.IsNullOrEmpty(remotePath)
            ? objectKey
            : $"{remotePath.TrimEnd('/')}/{objectKey}";

        _logger.LogInformation("Job {JobId}: Uploading file via ObjectStorage to {Path}", job.JobId, fullPath);

        // Calculate checksum before upload
        var checksum = await CalculateChecksumAsync(sourceVideoPath, ct);
        var fileSize = new FileInfo(sourceVideoPath).Length;

        // Upload the file
        await using (var sourceStream = File.OpenRead(sourceVideoPath))
        {
            await storage.WriteAsync(fullPath, sourceStream, false, ct);
        }

        _logger.LogInformation("Job {JobId}: File uploaded successfully to {Path}, checksum: {Checksum}",
            job.JobId, fullPath, checksum);

        await _messageBus.PublishAsync(Subjects.FileStaged, new FileStagedEvent
        {
            JobId = job.JobId,
            LocalPath = fullPath,
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
