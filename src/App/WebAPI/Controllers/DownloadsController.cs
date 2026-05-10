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
    /// Submits a new download/archive job. Publishes <see cref="DownloadRequested"/> to JetStream;
    /// DataBridge consumes it from <c>FROSTSTREAM_DOWNLOAD</c> and starts a Cleipnir flow keyed
    /// by the returned <c>JobId</c>. The caller may pass an inline yt-dlp options snapshot via
    /// <see cref="DownloadRequest.YtDlpOptions"/>; for a stored preset, see
    /// <see cref="SubmitWithPreset"/>.
    /// </summary>
    [HttpPost]
    public Task<ActionResult<DownloadRequestResponse>> Submit(
        [FromBody] DownloadRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            requestedBy: request.RequestedBy,
            tags: request.Tags,
            mediaKind: request.MediaKind,
            audioFormat: request.AudioFormat,
            ytDlpOptions: request.YtDlpOptions,
            presetKey: null,
            cookieKey: request.CookieKey,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Submits a download job whose yt-dlp options are resolved from a stored preset
    /// (managed via <see cref="OptionPresetsController"/>). Mutually exclusive with
    /// inline options on <see cref="Submit"/>.
    /// </summary>
    [HttpPost("preset")]
    public Task<ActionResult<DownloadRequestResponse>> SubmitWithPreset(
        [FromBody] DownloadPresetRequest request,
        CancellationToken cancellationToken)
        => PublishRequestAsync(
            sourceUrl: request.SourceUrl,
            storageKey: request.StorageKey,
            forceDownload: request.ForceDownload,
            requestedBy: request.RequestedBy,
            tags: request.Tags,
            mediaKind: request.MediaKind,
            audioFormat: request.AudioFormat,
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

    /// <summary>What kind of media to produce. Defaults to <see cref="MediaKind.Video"/>.</summary>
    [DefaultValue(MediaKind.Video)]
    public MediaKind MediaKind { get; init; } = MediaKind.Video;

    /// <summary>
    /// Audio format used when <see cref="MediaKind"/> is <see cref="MediaKind.Audio"/>.
    /// Ignored for video jobs. <c>null</c> defers to the worker's default (m4a).
    /// </summary>
    public AudioConversionFormat? AudioFormat { get; init; }

    /// <summary>
    /// Inline yt-dlp options snapshot. Worker merges this on top of its own defaults
    /// before invoking yt-dlp.
    /// </summary>
    public YtDlpOptions? YtDlpOptions { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

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

    /// <summary>What kind of media to produce. Defaults to <see cref="MediaKind.Video"/>.</summary>
    [DefaultValue(MediaKind.Video)]
    public MediaKind MediaKind { get; init; } = MediaKind.Video;

    /// <summary>Audio format used when <see cref="MediaKind"/> is <see cref="MediaKind.Audio"/>.</summary>
    public AudioConversionFormat? AudioFormat { get; init; }

    /// <summary>Stored option-preset key (see <see cref="OptionPresetsController"/>).</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string PresetKey { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

public sealed record DownloadRequestResponse(Guid JobId, Guid CorrelationId);
