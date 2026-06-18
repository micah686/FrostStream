using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;
using WebAPI.Features.Common;
using WebAPI.Features.Downloads.Models;
using WebAPI.Features.OptionPresets.Controllers;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.Downloads.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController(
    IJetStreamPublisher publisher,
    IClock clock,
    ILogger<DownloadsController> logger) : ControllerBase
{
    /// <summary>
    /// Submits a simple video download. No yt-dlp options, no presets, no audio
    /// toggles — just URL, storage, and optional cookie. Use
    /// <see cref="SubmitWithPreset"/> for anything more elaborate.
    /// </summary>
    [HttpPost]
    [EndpointSummary("Queue a video download")]
    [EndpointDescription("Creates a new video download job and publishes it to the durable download stream. Blank storage keys use the default storage target; optional requester, tags, cookie credentials, and force-download behavior are included in the queued command. Returns job and correlation identifiers immediately without waiting for the download to complete.")]
    public Task<ActionResult<DownloadRequestResponse>> Download(
        [FromBody] DownloadRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            requestedBy: request.RequestedBy,
            tags: request.Tags,
            mediaKind: MediaKind.Video,
            audioFormat: null,
            ytDlpOptions: null,
            presetKey: null,
            cookieKey: request.CookieKey,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Submits a simple audio-only download. The Worker forces
    /// <c>--extract-audio --audio-format mp3</c>; no other options are configurable.
    /// </summary>
    [HttpPost("audio")]
    [EndpointSummary("Queue an MP3 audio download")]
    [EndpointDescription("Creates an audio-only download job that extracts and converts the source to MP3. The request is published asynchronously to the download stream, uses the default storage target when no storage key is supplied, and returns job and correlation identifiers before processing begins.")]
    public Task<ActionResult<DownloadRequestResponse>> DownloadAudio(
        [FromBody] DownloadAudioRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            requestedBy: request.RequestedBy,
            tags: request.Tags,
            mediaKind: MediaKind.Audio,
            audioFormat: AudioConversionFormat.Mp3,
            ytDlpOptions: null,
            presetKey: null,
            cookieKey: request.CookieKey,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Submits a download whose yt-dlp options come from a stored preset (managed
    /// via <see cref="OptionPresetsController"/>). Preset content drives every
    /// yt-dlp flag — including audio extraction if the preset configures it.
    /// </summary>
    [HttpPost("preset")]
    [EndpointSummary("Queue a download using an option preset")]
    [EndpointDescription("Creates a download job whose yt-dlp behavior is loaded from the named stored option preset by the downstream worker flow. The endpoint publishes the request asynchronously, supports the same storage, requester, tags, cookie, and force-download fields as a standard download, and returns tracking identifiers immediately.")]
    public Task<ActionResult<DownloadRequestResponse>> DownloadWithPreset(
        [FromBody] DownloadPresetRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            requestedBy: request.RequestedBy,
            tags: request.Tags,
            mediaKind: MediaKind.Video,
            audioFormat: null,
            ytDlpOptions: null,
            presetKey: request.PresetKey,
            cookieKey: request.CookieKey,
            cancellationToken: cancellationToken);

    private async Task<ActionResult<DownloadRequestResponse>> PublishRequestAsync(
        string sourceUrl,
        string? storageKey,
        bool forceDownload,
        string? requestedBy,
        IReadOnlyList<string>? tags,
        MediaKind mediaKind,
        AudioConversionFormat? audioFormat,
        YtDlpOptions? ytDlpOptions,
        string? presetKey,
        string? cookieKey,
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
            RequestedBy = requestedBy,
            StorageKey = string.IsNullOrWhiteSpace(storageKey) ? "default" : storageKey,
            Tags = tags,
            ForceDownload = forceDownload,
            MediaKind = mediaKind,
            AudioFormat = audioFormat,
            YtDlpOptions = ytDlpOptions,
            PresetKey = presetKey,
            CookieKey = cookieKey
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
}
