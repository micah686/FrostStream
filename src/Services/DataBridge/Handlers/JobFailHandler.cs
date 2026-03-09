using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Jobs;
using Shared.Messages;

namespace DataBridge.Handlers;

public class JobFailHandler : MessageHandlerBase<JobFailRequest, JobFailResponse>
{
    public JobFailHandler(
        FlySwattr.NATS.Abstractions.IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<JobFailHandler> logger)
        : base(messageBus, scopeFactory, logger)
    {
    }

    protected override string Subject => Subjects.JobFail;

    protected override async Task<JobFailResponse> HandleRequestAsync(
        FrostStreamDbContext db,
        JobFailRequest request,
        CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == request.JobId, cancellationToken);
        if (job != null)
        {
            var currentStatus = JobStatusCodec.Parse(job.Status);
            if (currentStatus == JobStatus.Completed)
            {
                Logger.LogWarning(
                    "Ignoring JobFail for already completed JobId {JobId}. Error: {Error}",
                    request.JobId,
                    request.ErrorMessage);
                return new JobFailResponse(Success: true);
            }

            JobStateMachine.Transition(job, JobStatus.Failed);
            job.ErrorMsg = request.ErrorMessage;
            job.RetryCount++;

            var tracker = await db.JobTrackers.FirstOrDefaultAsync(t => t.JobId == request.JobId, cancellationToken);
            if (tracker != null)
            {
                tracker.ErrorDetails = request.ErrorDetails;
                tracker.RetryCount++;
                tracker.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new JobFailResponse(Success: true);
    }

    protected override JobFailResponse CreateErrorResponse(Exception exception)
    {
        // Even on error, return success to acknowledge receipt
        return new JobFailResponse(Success: true);
    }
}
