using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Entities;
using Shared.Messages;

namespace DataBridge.Handlers;

/// <summary>
/// Handles requests to get the next version number for a media item.
/// Reserves version numbers before upload to enable versioned storage paths.
/// </summary>
public class GetNextVersionHandler : MessageHandlerBase<GetNextVersionRequest, GetNextVersionResponse>
{
    public GetNextVersionHandler(
        FlySwattr.NATS.Abstractions.IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<GetNextVersionHandler> logger)
        : base(messageBus, scopeFactory, logger)
    {
    }

    protected override string Subject => Subjects.GetNextVersion;

    protected override async Task<GetNextVersionResponse> HandleRequestAsync(
        FrostStreamDbContext db,
        GetNextVersionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Look up the video info by idempotency key
            var videoInfo = await db.VideoInfos
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.IdempotencyKey == request.IdempotencyKey, cancellationToken);

            int nextVersion;

            if (videoInfo == null)
            {
                // No existing video, this will be version 1
                nextVersion = 1;
                Logger.LogInformation(
                    "Reserving version {VersionNum} for new video {Platform}/{MediaId} (IdempotencyKey: {IdempotencyKey})",
                    nextVersion, request.Platform, request.MediaId, request.IdempotencyKey);
            }
            else
            {
                // Get the max existing version for this video
                var maxVersion = await db.VideoVersions
                    .Where(v => v.VideoId == videoInfo.Id)
                    .MaxAsync(v => (int?)v.VersionNum, cancellationToken) ?? 0;

                nextVersion = maxVersion + 1;
                Logger.LogInformation(
                    "Reserving version {VersionNum} for existing video {VideoId} ({Platform}/{MediaId})",
                    nextVersion, videoInfo.Id, request.Platform, request.MediaId);
            }

            return new GetNextVersionResponse(true, nextVersion, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Failed to get next version for {Platform}/{MediaId} (IdempotencyKey: {IdempotencyKey})",
                request.Platform, request.MediaId, request.IdempotencyKey);
            return new GetNextVersionResponse(false, 0, $"Failed to determine next version: {ex.Message}");
        }
    }

    protected override GetNextVersionResponse CreateErrorResponse(Exception exception)
    {
        return new GetNextVersionResponse(false, 0, exception.Message);
    }
}
