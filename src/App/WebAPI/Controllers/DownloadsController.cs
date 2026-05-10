using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace WebAPI.Controllers;

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

/// <summary>Body for <see cref="DownloadsController.Download"/> — simple video download.</summary>
public sealed class DownloadRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public required string StorageKey { get; init; }

    [DefaultValue(false)]
    public bool ForceDownload { get; init; } = false;

    public string? RequestedBy { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

/// <summary>Body for <see cref="DownloadsController.DownloadAudio"/> — simple audio download (always MP3).</summary>
public sealed class DownloadAudioRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public required string StorageKey { get; init; }

    [DefaultValue(false)]
    public bool ForceDownload { get; init; } = false;

    public string? RequestedBy { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

/// <summary>Body for <see cref="DownloadsController.DownloadWithPreset"/> — download driven by a stored option preset.</summary>
public sealed class DownloadPresetRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public required string StorageKey { get; init; }

    [DefaultValue(false)]
    public bool ForceDownload { get; init; } = false;

    public string? RequestedBy { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Stored option-preset key (see <see cref="OptionPresetsController"/>).</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string PresetKey { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

public sealed record DownloadRequestResponse(Guid JobId, Guid CorrelationId);
