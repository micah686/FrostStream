using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Jobs;
using Shared.Messages;

namespace DataBridge.Handlers;

public class JobStatusHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobStatusHandler> _logger;

    public JobStatusHandler(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobStatusHandler> logger)
    {
        _messageBus = messageBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobStatusHandler subscribing to {Subject}", Subjects.JobStatus);

        await _messageBus.SubscribeAsync<JobStatusRequest>(
            Subjects.JobStatus,
            async context =>
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == context.Message.JobId, stoppingToken);
                if (job == null)
                {
                    await context.RespondAsync(
                        new JobStatusResponse(JobStatus.NotFound.ToStorageValue(), "Job not found in database", 0, null),
                        stoppingToken);
                    return;
                }

                await context.RespondAsync(new JobStatusResponse(job.Status, job.ErrorMsg, job.RetryCount, job.StorageKey), stoppingToken);
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }
}
