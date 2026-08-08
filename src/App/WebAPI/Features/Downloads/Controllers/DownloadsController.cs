using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Common;
using WebAPI.Features.DownloadConfigSets;
using WebAPI.Features.Downloads.Models;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.Downloads.Controllers;

[ApiController]
[Route("api/downloads")]
public class DownloadsController(
    IJetStreamPublisher publisher,
    IMessageBus messageBus,
    IClock clock,
    ILogger<DownloadsController> logger) : ControllerBase
{
    /// <summary>
    /// Submits a video download. Callers supply yt-dlp options directly (merged with the optional
    /// SponsorBlock section); audio-only capture is expressed through those options rather than a
    /// dedicated endpoint.
    /// </summary>
    [HttpPost("video")]
    [Endpoint(EndpointIds.DownloadsCreate)]
    [EndpointSummary("Queue a video download")]
    [EndpointDescription("Creates a new video download job and publishes it to the durable download stream. Blank storage keys use the default storage target; optional requester, tags, cookie credentials, yt-dlp options, and force-download behavior are included in the queued command. Supplied yt-dlp options are passed through to the worker's yt-dlp invocation. Returns job and correlation identifiers immediately without waiting for the download to complete. Unambiguous playlist-container URLs are auto-routed into the playlist pipeline instead and return a playlist identifier (kind \"playlist\") rather than a job identifier; force-download and tags do not apply on that path.")]
    public Task<ActionResult<DownloadRequestResponse>> Download(
        [FromBody] DownloadRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            tags: request.Tags,
            ytDlpOptions: CombineOptions(request.YtDlpOptions, request.SponsorBlock),
            cookieProfileKey: request.CookieProfileKey,
            priority: request.Priority,
            fetchComments: request.FetchComments,
            configSetKey: request.ConfigSetKey,
            cancellationToken: cancellationToken);

    private async Task<ActionResult<DownloadRequestResponse>> PublishRequestAsync(
        string sourceUrl,
        string? storageKey,
        bool forceDownload,
        IReadOnlyList<string>? tags,
        YtDlpOptions? ytDlpOptions,
        string? cookieProfileKey,
        int? priority,
        bool fetchComments,
        string? configSetKey,
        CancellationToken cancellationToken)
    {
        if (!YtDlpSourceUrlValidator.TryValidate(sourceUrl, out var validationError))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid source URL",
                Detail = validationError,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var subject = AuthConstants.FindSubject(User);

        // Same merge the playlist/creator-monitor endpoints use: an explicit field wins over the
        // config set's stored value, which wins over the system default. Also resolves the
        // user-owned cookie profile to a subject-scoped secret path.
        var (resolved, resolveError) = await DownloadConfigSetResolver.ResolveAsync(
            messageBus,
            subject,
            configSetKey,
            storageKey,
            cookieProfileKey,
            ytDlpOptions,
            encodeForPlaylistOverride: false,
            priorityOverride: priority,
            fetchCommentsOverride: fetchComments,
            cancellationToken);
        if (resolveError is not null)
            return BadRequest(resolveError);

        // Playlist-container URLs on the direct path would become a single unmodeled job (no
        // fan-out, no per-entry tracking), so route them into the playlist pipeline instead.
        if (PlaylistUrlDetector.IsPlaylistUrl(sourceUrl))
        {
            return await PublishPlaylistRequestAsync(sourceUrl, subject, resolved!, cancellationToken);
        }

        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var message = new DownloadRequested
        {
            JobId = jobId,
            CorrelationId = correlationId,
            CausationId = null,
            MessageId = messageId,
            OperationKey = $"job/{jobId:N}/requested",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            SourceUrl = sourceUrl,
            // Stamp the validated token subject, never client-supplied text, so "requested by" is trustworthy.
            RequestedBy = subject,
            StorageKey = resolved!.StorageKey,
            WorkerTag = resolved.WorkerTag,
            Tags = tags,
            ForceDownload = forceDownload,
            MediaKind = MediaKind.Video,
            AudioFormat = null,
            SourceKind = DownloadSourceKind.Direct,
            YtDlpOptions = resolved.YtDlpOptions,
            PresetKey = null,
            CookieSecretPath = resolved.CookieSecretPath,
            Priority = resolved.Priority,
            FetchComments = fetchComments
        };

        try
        {
            var group = new DownloadGroupRequested
            {
                GroupId = correlationId,
                CorrelationId = correlationId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"group/{correlationId:N}/requested",
                OccurredAt = message.OccurredAt,
                Kind = DownloadGroupKind.Direct,
                SourceUrl = sourceUrl,
                RequestedBy = subject,
                StorageKey = message.StorageKey,
                WorkerTag = resolved.WorkerTag,
                Priority = resolved.Priority,
                DirectRequest = message
            };
            await publisher.PublishAsync(
                DownloadSubjects.GroupRequested,
                group,
                messageId: group.MessageId.ToString("N"),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing DownloadRequested for JobId {JobId}", jobId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to submit download request",
                Detail = "Could not publish to the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        return Accepted(new DownloadRequestResponse(jobId, correlationId));
    }

    /// <summary>
    /// Routes a playlist-container URL into the playlist pipeline, mirroring
    /// <c>PlaylistsController.Submit</c>. The direct request's already-resolved config (storage,
    /// cookies, options, priority, comments, config set, worker tag) carries straight over.
    /// ForceDownload and Tags cannot be represented on <see cref="PlaylistRequested"/> and are
    /// dropped; per-entry force is available later via the playlist force-queue endpoint.
    /// </summary>
    private async Task<ActionResult<DownloadRequestResponse>> PublishPlaylistRequestAsync(
        string sourceUrl,
        string? subject,
        ResolvedDownloadConfigSet resolved,
        CancellationToken cancellationToken)
    {
        var playlistId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var message = new PlaylistRequested
        {
            PlaylistId = playlistId,
            CorrelationId = correlationId,
            CausationId = null,
            MessageId = messageId,
            OperationKey = $"playlist/{playlistId:N}/requested",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            SourceUrl = sourceUrl,
            RequestedBy = subject,
            StorageKey = resolved.StorageKey,
            ConfigSetKey = resolved.ConfigSetKey,
            WorkerTag = resolved.WorkerTag,
            EncodeForPlaylist = false,
            CookieSecretPath = resolved.CookieSecretPath,
            YtDlpOptions = resolved.YtDlpOptions,
            Priority = resolved.Priority,
            FetchComments = resolved.FetchComments
        };

        try
        {
            var group = new DownloadGroupRequested
            {
                GroupId = correlationId,
                CorrelationId = correlationId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"group/{correlationId:N}/requested",
                OccurredAt = message.OccurredAt,
                Kind = DownloadGroupKind.Playlist,
                SourceUrl = sourceUrl,
                RequestedBy = subject,
                StorageKey = message.StorageKey,
                WorkerTag = resolved.WorkerTag,
                Priority = resolved.Priority,
                CollectionRequest = message
            };
            await publisher.PublishAsync(
                DownloadSubjects.GroupRequested,
                group,
                messageId: group.MessageId.ToString("N"),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing PlaylistRequested for auto-routed playlist {PlaylistId}", playlistId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to submit playlist request",
                Detail = "Could not publish to the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        logger.LogInformation(
            "Auto-routed playlist URL from the direct download endpoint into the playlist pipeline as PlaylistId {PlaylistId}.",
            playlistId);
        return Accepted(new DownloadRoutedToPlaylistResponse(playlistId, correlationId));
    }

    /// <summary>Caller-supplied options form the base; a SponsorBlock section replaces their SponsorBlock group.</summary>
    private static YtDlpOptions? CombineOptions(YtDlpOptions? baseOptions, SponsorBlockRequest? sponsorBlock)
    {
        var sponsorBlockOptions = BuildYtDlpOptions(sponsorBlock);
        if (baseOptions is null)
            return sponsorBlockOptions;
        if (sponsorBlockOptions is null)
            return baseOptions;

        return baseOptions with { SponsorBlock = sponsorBlockOptions.SponsorBlock };
    }

    private static YtDlpOptions? BuildYtDlpOptions(SponsorBlockRequest? sponsorBlock)
    {
        if (sponsorBlock is null)
            return null;

        var markCategories = Normalize(sponsorBlock.MarkCategories);
        var removeCategories = Normalize(sponsorBlock.RemoveCategories);
        var chapterTitleTemplate = Normalize(sponsorBlock.ChapterTitleTemplate);
        var apiUrl = Normalize(sponsorBlock.ApiUrl);

        if (!sponsorBlock.Disable &&
            markCategories is null &&
            removeCategories is null &&
            chapterTitleTemplate is null &&
            apiUrl is null)
        {
            return null;
        }

        return new YtDlpOptions
        {
            SponsorBlock = new YtDlpSponsorBlockOptions
            {
                SponsorblockMark = markCategories,
                SponsorblockRemove = removeCategories,
                SponsorblockChapterTitle = chapterTitleTemplate,
                SponsorblockApi = apiUrl,
                NoSponsorblock = sponsorBlock.Disable
            }
        };
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
