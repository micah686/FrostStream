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

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                try
                {
                    var tracker = await db.JobTrackers
                        .Include(t => t.Job)
                        .FirstOrDefaultAsync(t => t.JobId == request.JobId, stoppingToken);

                    if (tracker == null || tracker.Job == null)
                    {
                        await context.RespondAsync(new VideoCommitResponse(false, "JobTracker not found"), stoppingToken);
                        return;
                    }

                    // Upsert VideoInfo
                    var videoInfo = await db.VideoInfos.FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, stoppingToken);
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

                    // Insert VideoVersion
                    var maxVersion = await db.VideoVersions
                        .Where(v => v.VideoId == videoInfo.Id)
                        .MaxAsync(v => (int?)v.VersionNum, stoppingToken) ?? 0;

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

                    tracker.VideoId = videoInfo.Id;
                    tracker.StoragePath = request.StoragePath;
                    tracker.FileHash = request.FileHash;
                    tracker.CompletedAt = DateTime.UtcNow;
                    tracker.UpdatedAt = DateTime.UtcNow;

                    tracker.Job.Status = "Completed";

                    await db.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);

                    await context.RespondAsync(new VideoCommitResponse(true, null), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to commit video for JobId: {JobId}", request.JobId);
                    await transaction.RollbackAsync(stoppingToken);
                    await context.RespondAsync(new VideoCommitResponse(false, ex.Message), stoppingToken);
                }
            },
            queueGroup: "databridge-jobs",
            cancellationToken: stoppingToken);
    }
}
