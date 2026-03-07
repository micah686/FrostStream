using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Entities;
using Shared.Jobs;
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

                var response = await HandleJobStartAsync(db, request, stoppingToken);
                await context.RespondAsync(response, stoppingToken);
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }

    private async Task<JobStartResponse> HandleJobStartAsync(
        FrostStreamDbContext db,
        JobStartRequest request,
        CancellationToken ct)
    {
        // Fast path: idempotency key already reserved.
        var existingTracker = await db.JobTrackers
            .Include(t => t.Job)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == request.IdempotencyKey, ct);

        if (existingTracker != null)
        {
            return await HandleExistingIdempotencyAsync(db, request, existingTracker, ct);
        }

        var now = DateTime.UtcNow;

        var newJob = new Job
        {
            JobId = request.JobId,
            Url = request.VideoUrl,
            Status = JobStatus.Processing.ToStorageValue(),
            StorageKey = request.StorageKey
        };

        var newTracker = new JobTracker
        {
            Id = Guid.NewGuid(),
            JobId = request.JobId,
            IdempotencyKey = request.IdempotencyKey,
            StorageKey = request.StorageKey,
            UpdatedAt = now,
            ExpiresAt = now.AddDays(1)
        };

        db.Jobs.Add(newJob);
        db.JobTrackers.Add(newTracker);

        try
        {
            await db.SaveChangesAsync(ct);
            return new JobStartResponse(Proceed: true, Reason: null);
        }
        catch (DbUpdateException ex) when (DbExceptionHelpers.IsUniqueViolation(ex))
        {
            // H2: concurrent identical submissions can race between read and insert.
            // Re-query and return deterministic response instead of bubbling constraint errors.
            _logger.LogWarning(ex,
                "JobStart unique constraint race for JobId {JobId}, IdempotencyKey {IdempotencyKey}",
                request.JobId, request.IdempotencyKey);

            db.ChangeTracker.Clear();

            var racedTracker = await db.JobTrackers
                .Include(t => t.Job)
                .FirstOrDefaultAsync(t => t.IdempotencyKey == request.IdempotencyKey, ct);

            if (racedTracker != null)
            {
                return await HandleExistingIdempotencyAsync(db, request, racedTracker, ct);
            }

            var existingJob = await db.Jobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.JobId == request.JobId, ct);

            if (existingJob != null)
            {
                return new JobStartResponse(Proceed: false, Reason: "DuplicateDelivery");
            }

            throw;
        }
    }

    private async Task<JobStartResponse> HandleExistingIdempotencyAsync(
        FrostStreamDbContext db,
        JobStartRequest request,
        JobTracker existingTracker,
        CancellationToken ct)
    {
        if (existingTracker.JobId == request.JobId)
        {
            if (existingTracker.Job == null)
            {
                _logger.LogWarning(
                    "Tracker exists without Job row for JobId {JobId}. Treating as duplicate delivery.",
                    request.JobId);
                return new JobStartResponse(Proceed: false, Reason: "DuplicateDelivery");
            }

            var currentStatus = JobStatusCodec.Parse(existingTracker.Job.Status);
            if (currentStatus == JobStatus.Failed)
            {
                _logger.LogInformation(
                    "Retrying failed job {JobId} (attempt {Attempt}).",
                    request.JobId, existingTracker.Job.RetryCount + 1);

                JobStateMachine.Transition(existingTracker.Job, JobStatus.Processing);
                existingTracker.Job.ErrorMsg = null;
                existingTracker.StoragePath = null;
                existingTracker.FileHash = null;
                existingTracker.CompletedAt = null;
                existingTracker.ErrorDetails = null;
                existingTracker.UpdatedAt = DateTime.UtcNow;
                existingTracker.ExpiresAt = DateTime.UtcNow.AddDays(1);

                await db.SaveChangesAsync(ct);
                return new JobStartResponse(Proceed: true, Reason: null);
            }

            _logger.LogWarning(
                "Duplicate delivery for JobId {JobId} with status {Status}. Ignoring.",
                request.JobId, existingTracker.Job.Status);
            return new JobStartResponse(Proceed: false, Reason: "DuplicateDelivery");
        }

        await CreateOrUpdatePendingLinkAsync(db, request, existingTracker, ct);
        return new JobStartResponse(Proceed: false, Reason: "AlreadyExists");
    }

    private async Task CreateOrUpdatePendingLinkAsync(
        FrostStreamDbContext db,
        JobStartRequest request,
        JobTracker sourceTracker,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Idempotency key {Key} already exists. Linking JobId {JobId} to source JobId {SourceJobId}.",
            request.IdempotencyKey,
            request.JobId,
            sourceTracker.JobId);

        var now = DateTime.UtcNow;

        var pendingJob = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == request.JobId, ct);
        if (pendingJob == null)
        {
            pendingJob = new Job
            {
                JobId = request.JobId,
                Url = request.VideoUrl,
                Status = JobStatus.PendingLink.ToStorageValue(),
                StorageKey = request.StorageKey
            };
            db.Jobs.Add(pendingJob);
        }
        else if (JobStatusCodec.Parse(pendingJob.Status) != JobStatus.Completed)
        {
            JobStateMachine.Transition(pendingJob, JobStatus.PendingLink);
            pendingJob.ErrorMsg = null;
        }

        var pendingLink = await db.PendingJobLinks
            .Include(l => l.PendingJob)
            .FirstOrDefaultAsync(l => l.PendingJobId == request.JobId, ct);

        if (pendingLink == null)
        {
            pendingLink = new PendingJobLink
            {
                Id = Guid.NewGuid(),
                PendingJobId = request.JobId,
                SourceJobId = sourceTracker.JobId,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = now
            };
            db.PendingJobLinks.Add(pendingLink);
        }
        else
        {
            pendingLink.SourceJobId = sourceTracker.JobId;
            pendingLink.IdempotencyKey = request.IdempotencyKey;
        }

        // If the source is already committed, resolve the link immediately.
        var existingVersion = await db.VideoVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, ct);

        if (existingVersion != null)
        {
            pendingLink.ExistingVersionId = existingVersion.Id;
            pendingLink.VideoId = existingVersion.VideoId;
            pendingLink.CompletedAt = now;

            if (pendingJob != null && JobStatusCodec.Parse(pendingJob.Status) != JobStatus.Completed)
            {
                JobStateMachine.Transition(pendingJob, JobStatus.Completed);
                pendingJob.ErrorMsg = null;
            }
        }
        else
        {
            pendingLink.ExistingVersionId = null;
            pendingLink.VideoId = null;
            pendingLink.CompletedAt = null;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptionHelpers.IsUniqueViolation(ex))
        {
            _logger.LogWarning(ex,
                "Pending link upsert raced for JobId {JobId}, IdempotencyKey {IdempotencyKey}",
                request.JobId, request.IdempotencyKey);
        }
    }
}
