using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Messaging;

namespace Worker.Services;

public sealed class WorkerHeartbeatService(
    IMessageBus messageBus,
    IOptions<WorkerOptions> options,
    IClock clock,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly string workerId = Environment.GetEnvironmentVariable("FROSTSTREAM_WORKER_ID")
        ?? $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configured = options.Value;
            try
            {
                await messageBus.PublishAsync(WorkerRegistrySubjects.Heartbeat, new WorkerHeartbeat
                {
                    WorkerId = workerId,
                    Name = string.IsNullOrWhiteSpace(configured.Name) ? Environment.MachineName : configured.Name.Trim(),
                    Tags = configured.Tags,
                    IncomingRoot = configured.IncomingRoot,
                    ReportedAt = clock.GetCurrentInstant()
                });
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not publish worker heartbeat."); }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
