using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace DataBridge;

/// <summary>
/// Background service that handles storage configuration requests and file staging events.
/// </summary>
public class DataBridgeService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<DataBridgeService> _logger;
    private readonly IConfiguration _configuration;

    // Default staging path - can be overridden via configuration
    private string StagingPath => _configuration["DataBridge:StagingPath"] ?? "../testing_data";

    // Default final destination path
    private string FinalDestinationBase => _configuration["DataBridge:FinalDestination"] ?? "../testing_data/completed";

    public DataBridgeService(
        IMessageBus messageBus,
        ILogger<DataBridgeService> logger,
        IConfiguration configuration)
    {
        _messageBus = messageBus;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataBridge service starting");

        // Ensure directories exist
        Directory.CreateDirectory(StagingPath);
        Directory.CreateDirectory(FinalDestinationBase);

        // Subscribe to storage configuration requests (request/reply pattern)
        await _messageBus.SubscribeAsync<StorageConfigRequest>(
            Subjects.StorageConfig,
            HandleStorageConfigRequestAsync,
            cancellationToken: stoppingToken);

        _logger.LogInformation("DataBridge subscribed to {Subject} for storage config requests",
            Subjects.StorageConfig);

        // Subscribe to file staged events
        await _messageBus.SubscribeAsync<FileStagedEvent>(
            Subjects.FileStaged,
            HandleFileStagedEventAsync,
            cancellationToken: stoppingToken);

        _logger.LogInformation("DataBridge subscribed to {Subject} for file staged events",
            Subjects.FileStaged);

        // Keep the service running
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleStorageConfigRequestAsync(IMessageContext<StorageConfigRequest> context)
    {
        var request = context.Message;
        _logger.LogInformation("Received storage config request for job {JobId} from worker {WorkerId}",
            request.JobId, request.WorkerId);

        // For now, always return PosixLocal method with a disk:// connection string.
        // Future: Could be dynamic based on job type, file size, configuration, etc.
        var fullStagingPath = Path.GetFullPath(StagingPath);
        var response = new StorageConfigResponse
        {
            Method = StorageMethod.PosixLocal,
            ConnectionString = $"disk://path={fullStagingPath}"
        };

        _logger.LogInformation("Responding to job {JobId} with storage method {Method}, connection: {Connection}",
            request.JobId, response.Method, response.ConnectionString);

        await context.RespondAsync(response);
    }

    private async Task HandleFileStagedEventAsync(IMessageContext<FileStagedEvent> context)
    {
        var evt = context.Message;
        _logger.LogInformation(
            "Received file staged event for job {JobId} from worker {WorkerId}. File: {Path}, Size: {Size} bytes, Checksum: {Checksum}",
            evt.JobId, evt.WorkerId, evt.LocalPath, evt.FileSizeBytes, evt.Checksum);

        try
        {
            if (!File.Exists(evt.LocalPath))
            {
                _logger.LogError("Job {JobId}: Staged file not found at {Path}", evt.JobId, evt.LocalPath);
                return;
            }

            // Determine final destination
            string finalPath;
            if (evt.FinalDestination.StartsWith("/") || evt.FinalDestination.Contains("://"))
            {
                // Absolute path or URL - use as-is
                finalPath = evt.FinalDestination;
            }
            else
            {
                // Relative path - combine with base destination
                finalPath = Path.Combine(FinalDestinationBase, evt.FinalDestination);
            }

            await ProcessStagedFileAsync(evt, finalPath);

            _logger.LogInformation("Job {JobId}: File processing completed. Final location: {Path}",
                evt.JobId, finalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: Failed to process staged file", evt.JobId);
        }
    }

    private async Task ProcessStagedFileAsync(FileStagedEvent evt, string finalDestination)
    {
        // Handle different destination types
        if (finalDestination.StartsWith("sftp://"))
        {
            // Scenario: Final destination is remote SFTP
            _logger.LogInformation("Job {JobId}: Would stream to SFTP destination (not implemented)", evt.JobId);
            // await StreamToSftpAsync(finalDestination, evt.LocalPath);
        }
        else
        {
            // Scenario: Local filesystem destination - just move the file (zero-copy if same filesystem)
            var destDir = Path.GetDirectoryName(finalDestination);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // If destination file exists, make a unique name
            var actualDestination = finalDestination;
            if (File.Exists(actualDestination))
            {
                var ext = Path.GetExtension(actualDestination);
                var baseName = Path.GetFileNameWithoutExtension(actualDestination);
                var dir = Path.GetDirectoryName(actualDestination) ?? ".";
                actualDestination = Path.Combine(dir, $"{baseName}_{evt.JobId}{ext}");
            }

            _logger.LogInformation("Job {JobId}: Moving file from {Source} to {Dest}",
                evt.JobId, evt.LocalPath, actualDestination);

            // Atomic move - zero bytes copied if on same filesystem
            File.Move(evt.LocalPath, actualDestination, overwrite: false);

            _logger.LogInformation("Job {JobId}: File moved successfully to {Path}", evt.JobId, actualDestination);
        }

        // Cleanup is handled by the move operation for local files
        // For remote destinations, we'd delete after successful upload:
        // File.Delete(evt.LocalPath);

        await Task.CompletedTask;
    }
    
}
