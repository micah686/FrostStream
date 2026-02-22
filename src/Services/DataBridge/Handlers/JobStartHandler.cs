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
                    if (existingTracker.JobId == request.JobId)
                    {
                        // Same message redelivered by NATS after a failure
                        if (existingTracker.Job?.Status == "Failed")
                        {
                            _logger.LogInformation("Retrying failed job {JobId} (attempt {Attempt}).", request.JobId, existingTracker.Job.RetryCount + 1);

                            // Reset state for retry — RetryCount was already incremented by JobFailHandler
                            existingTracker.Job.Status = "Processing";
                            existingTracker.Job.ErrorMsg = null;
                            existingTracker.StoragePath = null;
                            existingTracker.FileHash = null;
                            existingTracker.CompletedAt = null;
                            existingTracker.ErrorDetails = null;
                            existingTracker.UpdatedAt = DateTime.UtcNow;
                            existingTracker.ExpiresAt = DateTime.UtcNow.AddDays(1);
                            await db.SaveChangesAsync(stoppingToken);

                            await context.RespondAsync(new JobStartResponse(Proceed: true, Reason: null), stoppingToken);
                        }
                        else
                        {
                            // Still processing or already completed — ignore duplicate delivery
                            _logger.LogWarning("Duplicate delivery for JobId {JobId} with status {Status}. Ignoring.", request.JobId, existingTracker.Job?.Status);
                            await context.RespondAsync(new JobStartResponse(Proceed: false, Reason: "DuplicateDelivery"), stoppingToken);
                        }
                        return;
                    }

                    // Different job requesting the same content — create a PendingLink record
                    _logger.LogInformation("Idempotency key {Key} already exists. Creating PendingLink for JobId {JobId}.", request.IdempotencyKey, request.JobId);
                    var pendingJob = new Job
                    {
                        JobId = request.JobId,
                        Url = request.VideoUrl,
                        Status = "PendingLink",
                        StorageKey = request.StorageKey
                    };
                    db.Jobs.Add(pendingJob);
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
