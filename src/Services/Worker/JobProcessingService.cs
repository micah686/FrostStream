using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;
using Worker.Storage;

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
    private readonly StorageHandlerFactory _storageHandlerFactory;
    private readonly string _workerId;

    // Path to the source video file, configurable via Worker:SourceVideoPath
    private string SourceVideoPath => _configuration["Worker:SourceVideoPath"] ?? "video.mp4";

    public JobProcessingService(
        IMessageBus messageBus,
        ILogger<JobProcessingService> logger,
        IConfiguration configuration,
        StorageHandlerFactory storageHandlerFactory)
    {
        _messageBus = messageBus;
        _logger = logger;
        _configuration = configuration;
        _storageHandlerFactory = storageHandlerFactory;
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

            // Use the factory to get the appropriate handler
            var handler = _storageHandlerFactory.GetHandler(storageConfig.Method);
            await handler.HandleAsync(job, storageConfig, _workerId, SourceVideoPath, ct);

            _logger.LogInformation("Job {JobId}: Completed successfully", job.JobId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId}: Processing was cancelled", job.JobId);
        }
        catch (NotImplementedException ex)
        {
            _logger.LogWarning("Job {JobId}: {Message}", job.JobId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: Processing failed", job.JobId);
        }
    }
}
