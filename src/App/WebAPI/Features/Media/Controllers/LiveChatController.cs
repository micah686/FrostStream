using System.Text.RegularExpressions;
using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.LiveChat;
using Shared.Messaging;
using Shared.Storage;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// Live chat replay surface: playback-oriented message windows plus the archived custom emote
/// images they reference. Chat lives in the optional ClickHouse store, so every action returns
/// 404 when the feature is disabled rather than burning the DataBridge request timeout.
/// </summary>
[ApiController]
[Route("api/media/watch")]
public sealed partial class LiveChatController(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    IStoreProvider blobStorageProvider,
    MediaAccessChecker accessChecker,
    IOptions<LiveChatOptions> liveChatOptions,
    IConfiguration configuration,
    IClock clock,
    ILogger<LiveChatController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private readonly LiveChatOptions _liveChat = liveChatOptions.Value;

    /// <summary>
    /// Storage backend the Worker's asset cache writes emote images to. Same configuration key
    /// the Worker binds (<c>AssetCacheOptions</c>), so the two cannot drift apart.
    /// </summary>
    private readonly string _assetStorageKey =
        configuration["Cache:Assets:StorageKey"] is { Length: > 0 } key ? key : "default";

    [HttpGet("{mediaGuid:guid}/chat")]
    [Endpoint(EndpointIds.MediaChat)]
    [EndpointSummary("Read a window of archived live chat")]
    [EndpointDescription("Returns a chronological window of archived live chat messages for a media item, ordered by offset into the video. Pass around (milliseconds) with before/after counts to jump to a playback position after a seek, or from/to with an optional limit to page sequentially while playing. Messages carry fragments (text, emoji, custom emotes, links) plus Super Chat and membership details. Returns 404 when live chat replay is not enabled for this deployment.")]
    public async Task<IActionResult> GetChatWindow(
        Guid mediaGuid,
        [FromQuery] long? around = null,
        [FromQuery] int before = 200,
        [FromQuery] int after = 400,
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        [FromQuery] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!_liveChat.Enabled)
        {
            return NotFound("Live chat replay is not enabled for this deployment.");
        }

        if (before < 0 || after < 0 || limit <= 0)
        {
            return BadRequest("Query parameters 'before' and 'after' must not be negative, and 'limit' must be greater than zero.");
        }

        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        LiveChatWindowResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<LiveChatWindowRequestMessage, LiveChatWindowResponseMessage>(
                LiveChatSubjects.WindowGet,
                new LiveChatWindowRequestMessage
                {
                    MediaGuid = mediaGuid,
                    AroundMs = around,
                    Before = before,
                    After = after,
                    FromMs = from,
                    ToMs = to,
                    Limit = limit
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed reading the live chat window for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (!response.Success)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                response.ErrorMessage ?? "Live chat lookup failed.");
        }

        return Ok(response.Messages);
    }

    [HttpGet("chat/emotes/{fileName}")]
    [Endpoint(EndpointIds.MediaChatEmote)]
    [EndpointSummary("Get an archived custom chat emote image")]
    [EndpointDescription("Streams a custom channel emote image archived alongside a live chat replay. File names are content-addressed hashes produced during ingestion, so responses are immutable and safe to cache indefinitely.")]
    public async Task<IActionResult> GetEmote(string fileName, CancellationToken cancellationToken = default)
    {
        if (!_liveChat.Enabled)
        {
            return NotFound("Live chat replay is not enabled for this deployment.");
        }

        // Content-addressed names only: this both validates the shard layout below and makes
        // path traversal unrepresentable.
        if (!EmoteFileName().IsMatch(fileName))
        {
            return BadRequest("Emote file name is not a content-addressed asset name.");
        }

        var storagePath = $"assets/emotes/{fileName[..2]}/{fileName.Substring(2, 2)}/{fileName}";

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            _assetStorageKey,
            storagePath,
            subject: "chat emote",
            enableRangeProcessing: false,
            cacheControl: "public, max-age=31536000, immutable",
            cancellationToken: cancellationToken);
    }

    [HttpPost("chat/backfill")]
    [Endpoint(EndpointIds.MediaChatBackfill)]
    [EndpointSummary("Queue a live chat replay backfill")]
    [EndpointDescription("Publishes a background job that sweeps archived live streams for live_chat.json sidecars with no ingested chat replay and queues each for import. Use this after enabling live chat replay on an existing library, since chat sidecars are archived whether or not the feature is on. Pass mediaGuid to backfill a single video and force=true to re-ingest media that already have chat. Returns 202 once the request is accepted.")]
    public async Task<IActionResult> TriggerBackfill(
        [FromQuery] Guid? mediaGuid = null,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!_liveChat.Enabled)
        {
            return NotFound("Live chat replay is not enabled for this deployment.");
        }

        var now = clock.GetCurrentInstant();
        var request = BackgroundJobRequestFactory.CreateLiveChatBackfill(
            BackgroundJobRequestFactory.ManualScheduleKey,
            BackgroundJobRequestFactory.ManualLiveChatBackfillTaskType,
            now,
            now,
            mediaGuid,
            force);

        try
        {
            await publisher.PublishAsync(
                BackgroundJobSubjects.LiveChatBackfillRequest,
                request,
                request.IdempotencyKey,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing the live chat backfill request.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to publish the live chat backfill request.");
        }

        return Accepted();
    }

    [GeneratedRegex(@"^[0-9a-f]{32}\.(png|webp|gif|jpe?g)$")]
    private static partial Regex EmoteFileName();
}
