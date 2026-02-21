using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Entities;
using Shared.Messages;

namespace DataBridge.Handlers;

public class JobStartHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobStartHandler> _logger;

    public JobStartHandler(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobStartHandler> logger)
    {
        _messageBus = messageBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobStartHandler subscribing to {Subject}", Subjects.JobStart);

        await _messageBus.SubscribeAsync<JobStartRequest>(
            Subjects.JobStart,
            async context =>
            {
                var request = context.Message;
                _logger.LogInformation("Received JobStart for JobId: {JobId}", request.JobId);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                // Check if idempotency key already exists
                var existingTracker = await db.JobTrackers
                    .Include(t => t.Job)
                    .FirstOrDefaultAsync(t => t.IdempotencyKey == request.IdempotencyKey, stoppingToken);

                if (existingTracker != null)
                {
                    _logger.LogInformation("Idempotency key {Key} already exists. Skipping download for JobId {JobId}.", request.IdempotencyKey, request.JobId);
                    
                    // Create Job but mark as existing or pending link
                    var job = new Job
                    {
                        JobId = request.JobId,
                        Url = request.VideoUrl,
                        Status = "PendingLink",
                        StorageKey = request.StorageKey
                    };
                    db.Jobs.Add(job);
                    await db.SaveChangesAsync(stoppingToken);

                    await context.RespondAsync(new JobStartResponse(Proceed: false, Reason: "AlreadyExists"), stoppingToken);
                    return;
                }

                // Proceed with new download
                var newJob = new Job
                {
                    JobId = request.JobId,
                    Url = request.VideoUrl,
                    Status = "Processing",
                    StorageKey = request.StorageKey
                };
                
                var newTracker = new JobTracker
                {
                    Id = Guid.NewGuid(),
                    JobId = request.JobId,
                    IdempotencyKey = request.IdempotencyKey,
                    StorageKey = request.StorageKey,
                    UpdatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                };

                db.Jobs.Add(newJob);
                db.JobTrackers.Add(newTracker);
                await db.SaveChangesAsync(stoppingToken);

                await context.RespondAsync(new JobStartResponse(Proceed: true, Reason: null), stoppingToken);
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }
}
