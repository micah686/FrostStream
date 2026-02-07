using System.Security.Cryptography;
using FluentStorage;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Handles file transfer via POSIX-compatible local storage (local filesystem, NFS mounts, SMB/CIFS mounts).
/// Uses FluentStorage's DirectoryFiles provider to write to any directory-accessible path.
/// </summary>
public class PosixLocalHandler : IStorageHandler
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<PosixLocalHandler> _logger;

    public PosixLocalHandler(IMessageBus messageBus, ILogger<PosixLocalHandler> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    public StorageMethod SupportedMethod => StorageMethod.PosixLocal;

    public async Task HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            _logger.LogError("Job {JobId}: PosixLocal requires a connection string", job.JobId);
            return;
        }

        if (!File.Exists(sourceVideoPath))
        {
            _logger.LogError("Job {JobId}: Source video file not found at {Path}", job.JobId, sourceVideoPath);
            return;
        }

        // Parse the directory path from the connection string (disk://path=/some/dir)
        using var storage = StorageFactory.Blobs.FromConnectionString(config.ConnectionString);

        var tempFileName = $"{job.JobId}.part";
        var finalFileName = $"{job.JobId}.ready";

        _logger.LogInformation("Job {JobId}: Staging file via PosixLocal storage", job.JobId);

        // Write to temp name first
        await using (var sourceStream = File.OpenRead(sourceVideoPath))
        {
            await storage.WriteAsync(tempFileName, sourceStream, false, ct);
        }

        // Calculate checksum from the written file
        var checksum = await CalculateChecksumAsync(sourceVideoPath, ct);
        var fileSize = new FileInfo(sourceVideoPath).Length;

        // Rename to final name (atomic on local/NFS)
        // FluentStorage doesn't have a rename, so we re-write and delete temp
        await using (var sourceStream = await storage.OpenReadAsync(tempFileName, ct))
        {
            if (sourceStream != null)
            {
                await storage.WriteAsync(finalFileName, sourceStream, false, ct);
            }
        }
        await storage.DeleteAsync(new[] { tempFileName }, ct);

        _logger.LogInformation("Job {JobId}: File staged successfully as {FileName}, checksum: {Checksum}",
            job.JobId, finalFileName, checksum);

        // Build the full local path for the FileStagedEvent
        // Extract directory from connection string for the event
        var remotePath = config.RemotePath ?? "";
        var stagedPath = string.IsNullOrEmpty(remotePath)
            ? finalFileName
            : Path.Combine(remotePath, finalFileName);

        await _messageBus.PublishAsync(Subjects.FileStaged, new FileStagedEvent
        {
            JobId = job.JobId,
            LocalPath = stagedPath,
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
