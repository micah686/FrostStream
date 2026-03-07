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

public class JobProgressHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobProgressHandler> _logger;

    public JobProgressHandler(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobProgressHandler> logger)
    {
        _messageBus = messageBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobProgressHandler subscribing to {Subject}", Subjects.JobProgress);

        await _messageBus.SubscribeAsync<JobProgressRequest>(
            Subjects.JobProgress,
            async context =>
            {
                var request = context.Message;
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                var tracker = await db.JobTrackers
                    .Include(t => t.Job)
                    .FirstOrDefaultAsync(t => t.JobId == request.JobId, stoppingToken);

                if (tracker?.Job == null)
                {
                    await context.RespondAsync(new JobProgressResponse(false, "JobTracker not found"), stoppingToken);
                    return;
                }

                var targetStatus = JobStatusCodec.Parse(request.Status);
                if (targetStatus == JobStatus.Unknown || targetStatus == JobStatus.NotFound)
                {
                    await context.RespondAsync(new JobProgressResponse(false, $"Unsupported status: {request.Status}"), stoppingToken);
                    return;
                }

                try
                {
                    JobStateMachine.Transition(tracker.Job, targetStatus);
                }
                catch (InvalidOperationException ex)
                {
                    await context.RespondAsync(new JobProgressResponse(false, ex.Message), stoppingToken);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(request.StoragePath))
                {
                    tracker.StoragePath = request.StoragePath;
                }

                if (!string.IsNullOrWhiteSpace(request.FileHash))
                {
                    tracker.FileHash = request.FileHash;
                }

                tracker.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(stoppingToken);
                await context.RespondAsync(new JobProgressResponse(true, null), stoppingToken);
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }
}
