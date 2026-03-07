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

public class VideoCommitHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VideoCommitHandler> _logger;

    public VideoCommitHandler(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<VideoCommitHandler> logger)
    {
        _messageBus = messageBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VideoCommitHandler subscribing to {Subject}", Subjects.VideoCommit);

        await _messageBus.SubscribeAsync<VideoCommitRequest>(
            Subjects.VideoCommit,
            async context =>
            {
                var request = context.Message;
                _logger.LogInformation("Received VideoCommit for JobId: {JobId}", request.JobId);

                try
                {
                    var versionId = await CommitVideoAsync(request, stoppingToken);
                    if (versionId == null)
                    {
                        await context.RespondAsync(new VideoCommitResponse(false, "JobTracker not found"), stoppingToken);
                        return;
                    }

                    await TryPublishLinkCompletionAsync(request.JobId, versionId.Value, stoppingToken);
                    await context.RespondAsync(new VideoCommitResponse(true, null), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to commit video for JobId: {JobId}", request.JobId);
                    await context.RespondAsync(new VideoCommitResponse(false, ex.Message), stoppingToken);
                }
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }

    private async Task<Guid?> CommitVideoAsync(VideoCommitRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            var tracker = await db.JobTrackers
                .Include(t => t.Job)
                .FirstOrDefaultAsync(t => t.JobId == request.JobId, ct);

            if (tracker?.Job == null)
            {
                return null;
            }

            // C2: Idempotent commit behavior.
            var existingVersion = await db.VideoVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, ct);

            if (existingVersion != null)
            {
                ApplyCommittedState(tracker, existingVersion);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return existingVersion.Id;
            }

            var videoInfo = await db.VideoInfos
                .FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, ct);

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
                .MaxAsync(v => (int?)v.VersionNum, ct) ?? 0;

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

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return videoVersion.Id;
        }
        catch (DbUpdateException ex) when (DbExceptionHelpers.IsUniqueViolation(ex))
        {
            _logger.LogWarning(ex,
                "VideoCommit unique constraint race for JobId {JobId}, IdempotencyKey {IdempotencyKey}. Reconciling as committed.",
                request.JobId, request.IdempotencyKey);

            await transaction.RollbackAsync(ct);
            return await ReconcileCommittedStateAsync(request.JobId, request.IdempotencyKey, ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<Guid?> ReconcileCommittedStateAsync(
        Guid jobId,
        string idempotencyKey,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var tracker = await db.JobTrackers
            .Include(t => t.Job)
            .FirstOrDefaultAsync(t => t.JobId == jobId, ct);

        if (tracker?.Job == null)
        {
            return null;
        }

        var existingVersion = await db.VideoVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.IdempotencyKey == idempotencyKey, ct);

        if (existingVersion == null)
        {
            return null;
        }

        ApplyCommittedState(tracker, existingVersion);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
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

    private async Task TryPublishLinkCompletionAsync(Guid sourceJobId, Guid existingVersionId, CancellationToken ct)
    {
        try
        {
            await _messageBus.PublishAsync(
                Subjects.JobLinkComplete,
                new JobLinkCompleteRequest(sourceJobId, existingVersionId),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish JobLinkComplete for source JobId {JobId} and version {VersionId}.",
                sourceJobId, existingVersionId);
        }
    }
}
