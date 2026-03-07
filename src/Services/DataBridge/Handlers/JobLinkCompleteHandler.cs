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

public class JobLinkCompleteHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobLinkCompleteHandler> _logger;

    public JobLinkCompleteHandler(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobLinkCompleteHandler> logger)
    {
        _messageBus = messageBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobLinkCompleteHandler subscribing to {Subject}", Subjects.JobLinkComplete);

        await _messageBus.SubscribeAsync<JobLinkCompleteRequest>(
            Subjects.JobLinkComplete,
            async context =>
            {
                var request = context.Message;

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                var version = await db.VideoVersions.AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == request.ExistingVersionId, stoppingToken);

                if (version == null)
                {
                    _logger.LogWarning(
                        "JobLinkComplete received unknown version {VersionId} for source JobId {JobId}.",
                        request.ExistingVersionId,
                        request.JobId);
                    return;
                }

                var links = await db.PendingJobLinks
                    .Include(l => l.PendingJob)
                    .Where(l => l.SourceJobId == request.JobId && l.CompletedAt == null)
                    .ToListAsync(stoppingToken);

                if (links.Count == 0)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                foreach (var link in links)
                {
                    link.ExistingVersionId = version.Id;
                    link.VideoId = version.VideoId;
                    link.CompletedAt = now;

                    if (link.PendingJob != null
                        && JobStatusCodec.Parse(link.PendingJob.Status) != JobStatus.Completed)
                    {
                        JobStateMachine.Transition(link.PendingJob, JobStatus.Completed);
                        link.PendingJob.ErrorMsg = null;
                    }
                }

                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation(
                    "Resolved {Count} pending link job(s) for source JobId {JobId}.",
                    links.Count,
                    request.JobId);
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }
}
