using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace DataBridge.Handlers;

public class JobFailHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobFailHandler> _logger;

    public JobFailHandler(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobFailHandler> logger)
    {
        _messageBus = messageBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobFailHandler subscribing to {Subject}", Subjects.JobFail);

        await _messageBus.SubscribeAsync<JobFailRequest>(
            Subjects.JobFail,
            async context =>
            {
                var request = context.Message;
                _logger.LogInformation("Received JobFail for JobId: {JobId}", request.JobId);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                var job = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == request.JobId, stoppingToken);
                if (job != null)
                {
                    job.Status = "Failed";
                    job.ErrorMsg = request.ErrorMessage;
                    
                    var tracker = await db.JobTrackers.FirstOrDefaultAsync(t => t.JobId == request.JobId, stoppingToken);
                    if (tracker != null)
                    {
                        tracker.ErrorDetails = request.ErrorDetails;
                        tracker.UpdatedAt = DateTime.UtcNow;
                    }

                    await db.SaveChangesAsync(stoppingToken);
                }
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }
}
