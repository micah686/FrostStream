using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using Shared.Secrets;
using WebAPI.Auth;
using WebAPI.Features.Common;
using WebAPI.Features.Downloads.Models;
using WebAPI.Features.OptionPresets.Controllers;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.Downloads.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController(
    IJetStreamPublisher publisher,
    IMessageBus messageBus,
    IClock clock,
    ILogger<DownloadsController> logger) : ControllerBase
{
    private static readonly TimeSpan AdminRequestTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Submits a video download. Callers may supply yt-dlp options directly (merged with the
    /// optional SponsorBlock section); use <see cref="DownloadWithPreset"/> for stored yt-dlp
    /// option presets.
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
            mediaKind: MediaKind.Video,
            audioFormat: null,
            ytDlpOptions: CombineOptions(request.YtDlpOptions, request.SponsorBlock),
            presetKey: null,
            cookieProfileKey: request.CookieProfileKey,
            priority: request.Priority,
            fetchComments: request.FetchComments,
            // Only the plain video endpoint can auto-route: /audio's forced MP3 extraction and
            // /preset's PresetKey have no equivalent on PlaylistRequested.
            allowPlaylistAutoRoute: true,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Submits a simple audio-only download. The Worker forces
    /// <c>--extract-audio --audio-format mp3</c>; no other options are configurable.
    /// </summary>
    [HttpPost("audio")]
    [Endpoint(EndpointIds.DownloadsAudio)]
    [EndpointSummary("Queue an MP3 audio download")]
    [EndpointDescription("Creates an audio-only download job that extracts and converts the source to MP3. The request is published asynchronously to the download stream, uses the default storage target when no storage key is supplied, and returns job and correlation identifiers before processing begins.")]
    public Task<ActionResult<DownloadRequestResponse>> DownloadAudio(
        [FromBody] DownloadAudioRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            tags: request.Tags,
            mediaKind: MediaKind.Audio,
            audioFormat: AudioConversionFormat.Mp3,
            ytDlpOptions: BuildYtDlpOptions(request.SponsorBlock),
            presetKey: null,
            cookieProfileKey: request.CookieProfileKey,
            priority: request.Priority,
            fetchComments: request.FetchComments,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Submits a download whose yt-dlp options come from a stored preset (managed
    /// via <see cref="OptionPresetsController"/>). Preset content drives every
    /// yt-dlp flag — including audio extraction if the preset configures it.
    /// </summary>
    [HttpPost("preset")]
    [Endpoint(EndpointIds.DownloadsPreset)]
    [EndpointSummary("Queue a download using an option preset")]
    [EndpointDescription("Creates a download job whose yt-dlp behavior is loaded from the named stored option preset by the downstream worker flow. The endpoint publishes the request asynchronously, supports the same storage, requester, tags, cookie, and force-download fields as a standard download, and returns tracking identifiers immediately.")]
    public Task<ActionResult<DownloadRequestResponse>> DownloadWithPreset(
        [FromBody] DownloadPresetRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            tags: request.Tags,
            mediaKind: MediaKind.Video,
            audioFormat: null,
            ytDlpOptions: null,
            presetKey: request.PresetKey,
            cookieProfileKey: request.CookieProfileKey,
            priority: request.Priority,
            fetchComments: request.FetchComments,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Updates the scheduling priority of a download job. Only effective while the job
    /// is waiting for a download slot (<c>DownloadQueued</c> state); has no effect once
    /// the actual download has started.
    /// </summary>
    [HttpPatch("{jobId:guid}/priority")]
    [Endpoint(EndpointIds.DownloadsUpdatePriority)]
    [EndpointSummary("Update a download job's scheduling priority")]
    [EndpointDescription("Changes the priority (0–100) of a queued download job. Higher values run before lower ones. Effective only while the job is waiting for a download slot; no-ops if the download has already started.")]
    public async Task<ActionResult> UpdatePriority(
        [FromRoute] Guid jobId,
        [FromBody] UpdatePriorityRequest request,
        CancellationToken cancellationToken)
    {
        UpdateDownloadPriorityResponse? response;
        try
        {
            response = await messageBus.RequestAsync<UpdateDownloadPriorityRequest, UpdateDownloadPriorityResponse>(
                DownloadSubjects.UpdatePriorityRequest,
                new UpdateDownloadPriorityRequest { JobId = jobId, Priority = request.Priority },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting priority update for JobId {JobId}", jobId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to update priority",
                Detail = "Could not reach the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response is null || !response.Success)
        {
            var error = response?.Error ?? "No response from DataBridge.";
            if (error == "Job not found.")
                return NotFound(new ProblemDetails { Title = "Job not found.", Status = StatusCodes.Status404NotFound });
            return BadRequest(new ProblemDetails { Title = error, Status = StatusCodes.Status400BadRequest });
        }

        return NoContent();
    }

    /// <summary>
    /// Requests clean cancellation of a queued or actively downloading job. Cancellation is
    /// accepted asynchronously; the job moves through <c>Cancelling</c> before terminal
    /// <c>Cancelled</c> once the flow has released any held resources.
    /// </summary>
    [HttpPost("{jobId:guid}/cancel")]
    [Endpoint(EndpointIds.DownloadsRestartHalted)]
    [EndpointSummary("Cancel a download job")]
    [EndpointDescription("Requests clean cancellation of a queued or active download job. Queued jobs are removed from the scheduler; active yt-dlp processes are asked to stop and any worker-local temp files are cleaned.")]
    public async Task<ActionResult> Cancel(
        [FromRoute] Guid jobId,
        [FromBody] CancelDownloadApiRequest? request,
        CancellationToken cancellationToken)
    {
        CancelDownloadResponse? response;
        try
        {
            response = await messageBus.RequestAsync<CancelDownloadRequest, CancelDownloadResponse>(
                DownloadSubjects.CancelDownloadRequest,
                new CancelDownloadRequest
                {
                    JobId = jobId,
                    RequestedBy = AuthConstants.FindSubject(User),
                    Reason = request?.Reason
                },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting cancellation for JobId {JobId}", jobId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to cancel download",
                Detail = "Could not reach the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to cancel download",
                Detail = "No response from DataBridge.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response.Success)
            return Accepted(new CancelDownloadApiResponse(response.State ?? DownloadJobState.Cancelling));

        if (response.Error == "Job not found.")
            return NotFound(new ProblemDetails { Title = "Job not found.", Status = StatusCodes.Status404NotFound });

        return Conflict(new ProblemDetails
        {
            Title = response.Error ?? "Download cannot be cancelled.",
            Status = StatusCodes.Status409Conflict
        });
    }

    /// <summary>
    /// Restarts a download job from a restartable terminal state. Cancelled jobs replay as a
    /// fresh run; provider-halted jobs replay the original request payload and resume from the
    /// last recorded successful step when the flow has one available.
    /// </summary>
    [HttpPost("{jobId:guid}/restart")]
    [Endpoint(EndpointIds.DownloadsCancel)]
    [EndpointSummary("Restart a download job")]
    [EndpointDescription("Restarts a download job from a restartable terminal state. Cancelled jobs replay as a fresh run. Provider-halted jobs replay the original request and resume from the last recorded successful step when possible.")]
    public async Task<ActionResult> RestartHalted(
        [FromRoute] Guid jobId,
        CancellationToken cancellationToken)
    {
        RestartHaltedDownloadResponse? response;
        try
        {
            response = await messageBus.RequestAsync<RestartHaltedDownloadRequest, RestartHaltedDownloadResponse>(
                DownloadSubjects.RestartHaltedDownloadRequest,
                new RestartHaltedDownloadRequest
                {
                    JobId = jobId,
                    RequestedBy = AuthConstants.FindSubject(User)
                },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting restart for JobId {JobId}", jobId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to restart download",
                Detail = "Could not reach the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to restart download",
                Detail = "No response from DataBridge.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response.Success)
            return Accepted(new { State = "queued", JobId = response.JobId });

        return response.ErrorCode switch
        {
            "not_halted" => Conflict(new ProblemDetails { Title = response.ErrorMessage, Status = StatusCodes.Status409Conflict }),
            "missing_request" => StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = response.ErrorMessage, Status = StatusCodes.Status500InternalServerError }),
            _ => StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
            {
                Title = response.ErrorMessage ?? "Download cannot be restarted.",
                Status = StatusCodes.Status409Conflict
            })
        };
    }

    private async Task<ActionResult<DownloadRequestResponse>> PublishRequestAsync(
        string sourceUrl,
        string? storageKey,
        bool forceDownload,
        IReadOnlyList<string>? tags,
        MediaKind mediaKind,
        AudioConversionFormat? audioFormat,
        YtDlpOptions? ytDlpOptions,
        string? presetKey,
        string? cookieProfileKey,
        int priority,
        bool fetchComments,
        CancellationToken cancellationToken,
        bool allowPlaylistAutoRoute = false)
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

        // A user-owned cookie profile is resolved server-side to a subject-scoped secret path, so a
        // caller can only ever reference their own cookies — never a global key or another user's.
        string? cookieSecretPath = null;
        if (!string.IsNullOrWhiteSpace(cookieProfileKey))
        {
            if (!SecretPaths.IsValidUserScope(subject) || !SecretPaths.IsValidProfileKey(cookieProfileKey))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid cookie profile",
                    Detail = "cookieProfileKey must match ^[a-z0-9-]{2,100}$ for an authenticated user.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            cookieSecretPath = SecretPaths.ForUserCookieProfile(subject!, cookieProfileKey);
        }

        // Playlist-container URLs on the direct path would become a single unmodeled job (no
        // fan-out, no per-entry tracking), so route them into the playlist pipeline instead.
        if (allowPlaylistAutoRoute && PlaylistUrlDetector.IsPlaylistUrl(sourceUrl))
        {
            return await PublishPlaylistRequestAsync(
                sourceUrl, storageKey, subject, cookieSecretPath, ytDlpOptions, priority, fetchComments, cancellationToken);
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
            StorageKey = string.IsNullOrWhiteSpace(storageKey) ? "default" : storageKey,
            Tags = tags,
            ForceDownload = forceDownload,
            MediaKind = mediaKind,
            AudioFormat = audioFormat,
            SourceKind = DownloadSourceKind.Direct,
            YtDlpOptions = ytDlpOptions,
            PresetKey = presetKey,
            CookieSecretPath = cookieSecretPath,
            Priority = priority,
            FetchComments = fetchComments
        };

        try
        {
            await publisher.PublishAsync(
                DownloadSubjects.DownloadRequested,
                message,
                messageId: messageId.ToString("N"),
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
    /// <c>PlaylistsController.Submit</c>. The direct request already carries the equivalent
    /// config (storage, cookies, options, priority, comments); no config set is involved, which
    /// matches direct-path semantics (no ignore keywords, no playlist audio encoding).
    /// ForceDownload and Tags cannot be represented on <see cref="PlaylistRequested"/> and are
    /// dropped; per-entry force is available later via the playlist force-queue endpoint.
    /// </summary>
    private async Task<ActionResult<DownloadRequestResponse>> PublishPlaylistRequestAsync(
        string sourceUrl,
        string? storageKey,
        string? subject,
        string? cookieSecretPath,
        YtDlpOptions? ytDlpOptions,
        int priority,
        bool fetchComments,
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
            StorageKey = string.IsNullOrWhiteSpace(storageKey) ? "default" : storageKey,
            ConfigSetKey = null,
            EncodeForPlaylist = false,
            CookieSecretPath = cookieSecretPath,
            YtDlpOptions = ytDlpOptions,
            Priority = priority,
            FetchComments = fetchComments
        };

        try
        {
            await publisher.PublishAsync(
                PlaylistSubjects.PlaylistRequested,
                message,
                messageId: messageId.ToString("N"),
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
