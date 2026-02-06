using System.Security.Cryptography;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker;

/// <summary>
/// Background service that processes jobs from the NATS queue.
/// Multiple workers can run, using queue group for load balancing.
/// </summary>
public class JobProcessingService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<JobProcessingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _workerId;

    // Path to the source video file, configurable via Worker:SourceVideoPath
    private string SourceVideoPath => _configuration["Worker:SourceVideoPath"] ?? "video.mp4";

    public JobProcessingService(
        IMessageBus messageBus,
        ILogger<JobProcessingService> logger,
        IConfiguration configuration)
    {
        _messageBus = messageBus;
        _logger = logger;
        _configuration = configuration;
        _workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid():N}"[..32];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker {WorkerId} starting job processing service", _workerId);

        // Subscribe with queue group "workers" for load balancing across multiple worker instances
        await _messageBus.SubscribeAsync<ProcessJobRequest>(
            Subjects.JobProcess,
            async ctx => await HandleJobAsync(ctx, stoppingToken),
            queueGroup: "workers",
            cancellationToken: stoppingToken);

        _logger.LogInformation("Worker {WorkerId} subscribed to {Subject} with queue group 'workers'",
            _workerId, Subjects.JobProcess);

        // Keep the service running
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleJobAsync(IMessageContext<ProcessJobRequest> context, CancellationToken ct)
    {
        var job = context.Message;
        _logger.LogInformation("Worker {WorkerId} picked up job {JobId}", _workerId, job.JobId);

        try
        {
            // Simulate being busy for 5 seconds (processing work)
            _logger.LogInformation("Job {JobId}: Simulating processing work for 5 seconds...", job.JobId);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            // Request storage configuration from DataBridge
            _logger.LogInformation("Job {JobId}: Requesting storage configuration from DataBridge", job.JobId);
            var storageConfig = await _messageBus.RequestAsync<StorageConfigRequest, StorageConfigResponse>(
                Subjects.StorageConfig,
                new StorageConfigRequest
                {
                    JobId = job.JobId,
                    WorkerId = _workerId
                },
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: ct);

            if (storageConfig is null)
            {
                _logger.LogError("Job {JobId}: Failed to get storage configuration from DataBridge", job.JobId);
                return;
            }

            _logger.LogInformation("Job {JobId}: DataBridge returned storage method: {Method}",
                job.JobId, storageConfig.Method);

            // Handle based on storage method
            switch (storageConfig.Method)
            {
                case StorageMethod.LocalStaging:
                    await HandleLocalStagingAsync(job, storageConfig, ct);
                    break;

                case StorageMethod.DirectStreaming:
                case StorageMethod.ObjectStore:
                case StorageMethod.DirectExternal:
                    _logger.LogWarning("Job {JobId}: Storage method {Method} not yet implemented",
                        job.JobId, storageConfig.Method);
                    break;
            }

            _logger.LogInformation("Job {JobId}: Completed successfully", job.JobId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId}: Processing was cancelled", job.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: Processing failed", job.JobId);
        }
    }

    private async Task HandleLocalStagingAsync(ProcessJobRequest job, StorageConfigResponse config, CancellationToken ct)
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
        if (!File.Exists(SourceVideoPath))
        {
            _logger.LogError("Job {JobId}: Source video file not found at {Path}", job.JobId, SourceVideoPath);
            return;
        }

        await using (var sourceStream = File.OpenRead(SourceVideoPath))
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
            WorkerId = _workerId
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
