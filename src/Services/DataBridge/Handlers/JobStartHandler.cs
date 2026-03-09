using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Entities;
using Shared.Jobs;
using Shared.Messages;

namespace DataBridge.Handlers;

public class JobStartHandler : MessageHandlerBase<JobStartRequest, JobStartResponse>
{
    public JobStartHandler(
        FlySwattr.NATS.Abstractions.IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobStartHandler> logger)
        : base(messageBus, scopeFactory, logger)
    {
    }

    protected override string Subject => Subjects.JobStart;

    protected override async Task<JobStartResponse> HandleRequestAsync(
        FrostStreamDbContext db,
        JobStartRequest request,
        CancellationToken cancellationToken)
    {
        // Fast path: idempotency key already reserved.
        var existingTracker = await db.JobTrackers
            .Include(t => t.Job)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == request.IdempotencyKey, cancellationToken);

        if (existingTracker != null)
        {
            return await HandleExistingIdempotencyAsync(db, request, existingTracker, cancellationToken);
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
            await db.SaveChangesAsync(cancellationToken);
            return new JobStartResponse(Proceed: true, Reason: null);
        }
        catch (DbUpdateException ex) when (DbExceptionHelpers.IsUniqueViolation(ex))
        {
            // H2: concurrent identical submissions can race between read and insert.
            // Re-query and return deterministic response instead of bubbling constraint errors.
            Logger.LogWarning(ex,
                "JobStart unique constraint race for JobId {JobId}, IdempotencyKey {IdempotencyKey}",
                request.JobId, request.IdempotencyKey);

            db.ChangeTracker.Clear();

            var racedTracker = await db.JobTrackers
                .Include(t => t.Job)
                .FirstOrDefaultAsync(t => t.IdempotencyKey == request.IdempotencyKey, cancellationToken);

            if (racedTracker != null)
            {
                return await HandleExistingIdempotencyAsync(db, request, racedTracker, cancellationToken);
            }

            var existingJob = await db.Jobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.JobId == request.JobId, cancellationToken);

            if (existingJob != null)
            {
                return new JobStartResponse(Proceed: false, Reason: "DuplicateDelivery");
            }

            throw;
        }
    }

    protected override JobStartResponse CreateErrorResponse(Exception exception)
    {
        return new JobStartResponse(Proceed: false, Reason: exception.Message);
    }

    private async Task<JobStartResponse> HandleExistingIdempotencyAsync(
        FrostStreamDbContext db,
        JobStartRequest request,
        JobTracker existingTracker,
        CancellationToken cancellationToken)
    {
        if (existingTracker.JobId == request.JobId)
        {
            if (existingTracker.Job == null)
            {
                Logger.LogWarning(
                    "Tracker exists without Job row for JobId {JobId}. Treating as duplicate delivery.",
                    request.JobId);
                return new JobStartResponse(Proceed: false, Reason: "DuplicateDelivery");
            }

            var currentStatus = JobStatusCodec.Parse(existingTracker.Job.Status);
            if (currentStatus == JobStatus.Failed)
            {
                Logger.LogInformation(
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

                await db.SaveChangesAsync(cancellationToken);
                return new JobStartResponse(Proceed: true, Reason: null);
            }

            Logger.LogWarning(
                "Duplicate delivery for JobId {JobId} with status {Status}. Ignoring.",
                request.JobId, existingTracker.Job.Status);
            return new JobStartResponse(Proceed: false, Reason: "DuplicateDelivery");
        }

        await CreateOrUpdatePendingLinkAsync(db, request, existingTracker, cancellationToken);
        return new JobStartResponse(Proceed: false, Reason: "AlreadyExists");
    }

    private async Task CreateOrUpdatePendingLinkAsync(
        FrostStreamDbContext db,
        JobStartRequest request,
        JobTracker sourceTracker,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation(
            "Idempotency key {Key} already exists. Linking JobId {JobId} to source JobId {SourceJobId}.",
            request.IdempotencyKey,
            request.JobId,
            sourceTracker.JobId);

        var now = DateTime.UtcNow;

        var pendingJob = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == request.JobId, cancellationToken);
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
            .FirstOrDefaultAsync(l => l.PendingJobId == request.JobId, cancellationToken);

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
            .FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, cancellationToken);

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
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbExceptionHelpers.IsUniqueViolation(ex))
        {
            Logger.LogWarning(ex,
                "Pending link upsert raced for JobId {JobId}, IdempotencyKey {IdempotencyKey}",
                request.JobId, request.IdempotencyKey);
        }
    }
}
