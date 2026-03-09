using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Jobs;
using Shared.Messages;

namespace DataBridge.Handlers;

public class JobProgressHandler : MessageHandlerBase<JobProgressRequest, JobProgressResponse>
{
    public JobProgressHandler(
        FlySwattr.NATS.Abstractions.IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobProgressHandler> logger)
        : base(messageBus, scopeFactory, logger)
    {
    }

    protected override string Subject => Subjects.JobProgress;

    protected override async Task<JobProgressResponse> HandleRequestAsync(
        FrostStreamDbContext db,
        JobProgressRequest request,
        CancellationToken cancellationToken)
    {
        var tracker = await db.JobTrackers
            .Include(t => t.Job)
            .FirstOrDefaultAsync(t => t.JobId == request.JobId, cancellationToken);

        if (tracker?.Job == null)
        {
            return new JobProgressResponse(false, "JobTracker not found");
        }

        var targetStatus = JobStatusCodec.Parse(request.Status);
        if (targetStatus == JobStatus.Unknown || targetStatus == JobStatus.NotFound)
        {
            return new JobProgressResponse(false, $"Unsupported status: {request.Status}");
        }

        try
        {
            JobStateMachine.Transition(tracker.Job, targetStatus);
        }
        catch (InvalidOperationException ex)
        {
            return new JobProgressResponse(false, ex.Message);
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

        await db.SaveChangesAsync(cancellationToken);
        return new JobProgressResponse(true, null);
    }

    protected override JobProgressResponse CreateErrorResponse(Exception exception)
    {
        return new JobProgressResponse(false, exception.Message);
    }
}
