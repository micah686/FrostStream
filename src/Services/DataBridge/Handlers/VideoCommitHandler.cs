using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Entities;
using Shared.Jobs;
using Shared.Messages;

namespace DataBridge.Handlers;

public class VideoCommitHandler : MessageHandlerBase<VideoCommitRequest, VideoCommitResponse>
{
    public VideoCommitHandler(
        FlySwattr.NATS.Abstractions.IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<VideoCommitHandler> logger)
        : base(messageBus, scopeFactory, logger)
    {
    }

    protected override string Subject => Subjects.VideoCommit;

    protected override async Task<VideoCommitResponse> HandleRequestAsync(
        FrostStreamDbContext db,
        VideoCommitRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var tracker = await db.JobTrackers
                .Include(t => t.Job)
                .FirstOrDefaultAsync(t => t.JobId == request.JobId, cancellationToken);

            if (tracker?.Job == null)
            {
                return new VideoCommitResponse(false, "JobTracker not found");
            }

            // C2: Idempotent commit behavior.
            var existingVersion = await db.VideoVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, cancellationToken);

            if (existingVersion != null)
            {
                ApplyCommittedState(tracker, existingVersion);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                
                await TryPublishLinkCompletionAsync(request.JobId, existingVersion.Id, cancellationToken);
                return new VideoCommitResponse(true, null);
            }

            var videoInfo = await db.VideoInfos
                .FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, cancellationToken);

            if (videoInfo == null)
            {
                videoInfo = new VideoInfo
                {
                    Id = Guid.NewGuid(),
                    VideoUrl = tracker.Job.Url,
                    Platform = request.Platform,
                    SourceLastModified = request.SourceLastModified,
                    CreatedAt = DateTime.UtcNow,
                    MetadataJson = request.MetadataJson,
                    IdempotencyKey = request.IdempotencyKey,
                    IsDirty = false
                };
                db.VideoInfos.Add(videoInfo);
            }
            else
            {
                videoInfo.IsDirty = false;
                videoInfo.MetadataJson = request.MetadataJson;
            }

            var maxVersion = await db.VideoVersions
                .Where(v => v.VideoId == videoInfo.Id)
                .MaxAsync(v => (int?)v.VersionNum, cancellationToken) ?? 0;

            var videoVersion = new VideoVersion
            {
                Id = Guid.NewGuid(),
                VideoId = videoInfo.Id,
                IdempotencyKey = request.IdempotencyKey,
                FileHash = request.FileHash,
                StorageKey = request.StorageKey,
                StoragePath = request.StoragePath,
                VersionNum = maxVersion + 1
            };
            db.VideoVersions.Add(videoVersion);

            ApplyCommittedState(tracker, videoVersion);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            
            await TryPublishLinkCompletionAsync(request.JobId, videoVersion.Id, cancellationToken);
            return new VideoCommitResponse(true, null);
        }
        catch (DbUpdateException ex) when (DbExceptionHelpers.IsUniqueViolation(ex))
        {
            Logger.LogWarning(ex,
                "VideoCommit unique constraint race for JobId {JobId}, IdempotencyKey {IdempotencyKey}. Reconciling as committed.",
                request.JobId, request.IdempotencyKey);

            await transaction.RollbackAsync(cancellationToken);
            var versionId = await ReconcileCommittedStateAsync(request.JobId, request.IdempotencyKey, cancellationToken);
            
            if (versionId != null)
            {
                await TryPublishLinkCompletionAsync(request.JobId, versionId.Value, cancellationToken);
                return new VideoCommitResponse(true, null);
            }
            
            return new VideoCommitResponse(false, "Failed to reconcile committed state");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    protected override VideoCommitResponse CreateErrorResponse(Exception exception)
    {
        return new VideoCommitResponse(false, exception.Message);
    }

    private async Task<Guid?> ReconcileCommittedStateAsync(
        Guid jobId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var tracker = await db.JobTrackers
            .Include(t => t.Job)
            .FirstOrDefaultAsync(t => t.JobId == jobId, cancellationToken);

        if (tracker?.Job == null)
        {
            return null;
        }

        var existingVersion = await db.VideoVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existingVersion == null)
        {
            return null;
        }

        ApplyCommittedState(tracker, existingVersion);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existingVersion.Id;
    }

    private static void ApplyCommittedState(JobTracker tracker, VideoVersion version)
    {
        tracker.VideoId = version.VideoId;
        tracker.StoragePath = version.StoragePath;
        tracker.FileHash = version.FileHash;
        tracker.CompletedAt ??= DateTime.UtcNow;
        tracker.UpdatedAt = DateTime.UtcNow;
        tracker.ErrorDetails = null;

        if (tracker.Job != null)
        {
            if (JobStatusCodec.Parse(tracker.Job.Status) != JobStatus.Completed)
            {
                JobStateMachine.Transition(tracker.Job, JobStatus.Completed);
            }

            tracker.Job.ErrorMsg = null;
        }
    }

    private async Task TryPublishLinkCompletionAsync(Guid sourceJobId, Guid existingVersionId, CancellationToken cancellationToken)
    {
        try
        {
            await MessageBus.PublishAsync(
                Subjects.JobLinkComplete,
                new JobLinkCompleteRequest(sourceJobId, existingVersionId),
                cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to publish JobLinkComplete for source JobId {JobId} and version {VersionId}.",
                sourceJobId, existingVersionId);
        }
    }
}
