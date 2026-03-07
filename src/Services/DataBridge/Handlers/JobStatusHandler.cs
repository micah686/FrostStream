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

                var job = await db.Jobs.AsNoTracking()
                    .FirstOrDefaultAsync(j => j.JobId == context.Message.JobId, stoppingToken);

                if (job == null)
                {
                    await context.RespondAsync(
                        new JobStatusResponse(
                            context.Message.JobId,
                            JobStatus.NotFound.ToStorageValue(),
                            Phase: "NotFound",
                            SubStatus: "Job not found in database.",
                            ErrorMessage: "Job not found in database",
                            RetryCount: 0,
                            StorageKey: null,
                            StoragePath: null,
                            FileHash: null,
                            VideoId: null,
                            UpdatedAt: null,
                            CompletedAt: null,
                            PendingLink: null),
                        stoppingToken);
                    return;
                }

                var tracker = await db.JobTrackers.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.JobId == job.JobId, stoppingToken);

                var pendingLink = await db.PendingJobLinks.AsNoTracking()
                    .Include(l => l.SourceJob)
                    .FirstOrDefaultAsync(l => l.PendingJobId == job.JobId, stoppingToken);

                var status = JobStatusCodec.Parse(job.Status);
                var linkInfo = pendingLink == null
                    ? null
                    : new JobPendingLinkInfo(
                        pendingLink.SourceJobId,
                        pendingLink.SourceJob?.Status,
                        pendingLink.ExistingVersionId,
                        pendingLink.VideoId,
                        pendingLink.CreatedAt,
                        pendingLink.CompletedAt);

                var response = new JobStatusResponse(
                    JobId: job.JobId,
                    Status: job.Status,
                    Phase: MapPhase(status),
                    SubStatus: BuildSubStatus(status, tracker, pendingLink),
                    ErrorMessage: job.ErrorMsg,
                    RetryCount: job.RetryCount,
                    StorageKey: job.StorageKey,
                    StoragePath: tracker?.StoragePath,
                    FileHash: tracker?.FileHash,
                    VideoId: tracker?.VideoId ?? pendingLink?.VideoId,
                    UpdatedAt: tracker?.UpdatedAt ?? pendingLink?.CreatedAt,
                    CompletedAt: tracker?.CompletedAt ?? pendingLink?.CompletedAt,
                    PendingLink: linkInfo);

                await context.RespondAsync(response, stoppingToken);
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }

    private static string MapPhase(JobStatus status) =>
        status switch
        {
            JobStatus.Pending => "Queued",
            JobStatus.Processing => "Downloading",
            JobStatus.UploadedPendingCommit => "Committing",
            JobStatus.PendingLink => "Linking",
            JobStatus.Completed => "Completed",
            JobStatus.Failed => "Failed",
            JobStatus.NotFound => "NotFound",
            _ => "Unknown"
        };

    private static string? BuildSubStatus(
        JobStatus status,
        Shared.Entities.JobTracker? tracker,
        Shared.Entities.PendingJobLink? pendingLink)
    {
        return status switch
        {
            JobStatus.Pending => "Queued for worker pickup.",
            JobStatus.Processing => "Fetching metadata and downloading source media.",
            JobStatus.UploadedPendingCommit => string.IsNullOrWhiteSpace(tracker?.StoragePath)
                ? "Artifact upload completed; awaiting durable commit."
                : $"Artifact uploaded to {tracker.StoragePath}; awaiting durable commit.",
            JobStatus.PendingLink when pendingLink != null && pendingLink.CompletedAt == null =>
                pendingLink.SourceJob == null
                    ? $"Waiting for source job {pendingLink.SourceJobId} to finish."
                    : $"Waiting for source job {pendingLink.SourceJobId} ({pendingLink.SourceJob.Status}) to finish.",
            JobStatus.Completed when pendingLink != null =>
                pendingLink.ExistingVersionId == null
                    ? "Completed via duplicate-link resolution."
                    : $"Completed by linking to committed version {pendingLink.ExistingVersionId}.",
            JobStatus.Completed => "Worker upload and database commit completed successfully.",
            JobStatus.Failed => "Processing failed. Inspect error details for the latest failure.",
            JobStatus.NotFound => "Job not found in database.",
            _ => null
        };
    }
}
