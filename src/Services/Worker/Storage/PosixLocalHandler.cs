using System.IO.Hashing;
using FluentStorage;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Storage;

/// <summary>
/// Handles file transfer via POSIX-compatible local storage (local filesystem, NFS mounts, SMB/CIFS mounts).
/// Computes XxHash128 while writing to avoid a second read pass.
/// </summary>
public class PosixLocalHandler : IStorageHandler
{
    private readonly ILogger<PosixLocalHandler> _logger;

    public PosixLocalHandler(ILogger<PosixLocalHandler> logger)
    {
        _logger = logger;
    }

    public StorageMethod SupportedMethod => StorageMethod.PosixLocal;

    public async Task<StorageResult> HandleAsync(
        ProcessJobRequest job,
        StorageConfigResponse config,
        string workerId,
        string sourceVideoPath,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
            throw new InvalidOperationException($"Job {job.JobId}: PosixLocal requires a connection string");

        if (!File.Exists(sourceVideoPath))
            throw new FileNotFoundException($"Job {job.JobId}: Source video file not found at {sourceVideoPath}");

        using var storage = StorageFactory.Blobs.FromConnectionString(config.ConnectionString);

        var stagedFileName = $"{job.JobId}.part";

        _logger.LogInformation("Job {JobId}: Staging file via PosixLocal storage as {FileName}", job.JobId, stagedFileName);

        // Write to .part file while computing XxHash128 incrementally
        var hash = new XxHash128();
        long fileSize = 0;
        var buffer = new byte[81920];

        await using (var sourceStream = File.OpenRead(sourceVideoPath))
        await using (var memoryStream = new MemoryStream())
        {
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
            {
                hash.Append(buffer.AsSpan(0, bytesRead));
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                fileSize += bytesRead;
            }

            memoryStream.Position = 0;
            await storage.WriteAsync(stagedFileName, memoryStream, false, ct);
        }

        var xxHash = Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();

        var remotePath = config.RemotePath ?? "";
        var stagedPath = string.IsNullOrEmpty(remotePath)
            ? stagedFileName
            : Path.Combine(remotePath, stagedFileName);

        _logger.LogInformation("Job {JobId}: File staged as {Path}, XxHash128: {Hash}, Size: {Size} bytes",
            job.JobId, stagedPath, xxHash, fileSize);

        return new StorageResult(stagedPath, xxHash, fileSize);
    }
}
