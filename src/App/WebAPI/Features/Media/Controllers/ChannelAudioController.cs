using Conduit.NATS;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using Shared.Storage;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

[ApiController]
[Route("api/media/channels/{accountId:long}/audio")]
public sealed class ChannelAudioController(
    ChannelAudioResolver channelAudio,
    PodcastTokenService podcastTokens,
    IOptions<FrostStreamAuthOptions> authOptions,
    IStoreProvider blobStorageProvider,
    MediaAccessChecker accessChecker,
    IMessageBus messageBus,
    ILogger<ChannelAudioController> logger) : ControllerBase
{
    private static readonly TimeSpan EncodingStatusTimeout = TimeSpan.FromSeconds(10);


    [HttpGet("status")]
    [Endpoint(EndpointIds.ChannelAudioStatus)]
    [EndpointSummary("Get channel audio encoding progress")]
    [EndpointDescription("Returns aggregate missing, queued, processing, ready, and failed Opus rendition counts for every archived item in a channel, together with the ordered ready-item metadata used by the virtual audio playlist.")]
    public async Task<IActionResult> GetStatus(long accountId, [FromQuery] string? storageKey, CancellationToken cancellationToken)
    {
        var (error, channel) = await channelAudio.ResolveAsync(
            accountId, storageKey, createIfMissing: false, retryFailedAndPending: false, forceReencode: false, cancellationToken);
        return error ?? Ok(channel);
    }

    [HttpPost("encode")]
    [Endpoint(EndpointIds.ChannelAudioEncode)]
    [EndpointSummary("Encode a channel as Opus audio")]
    [EndpointDescription("Creates any missing Opus audio rendition records for archived items in the channel, retries failed renditions, and publishes idempotent MediaProcessor jobs for everything still waiting to be encoded. Set force=true to re-encode ready media too, including media already marked encoded in the database.")]
    public async Task<IActionResult> Encode(long accountId, [FromQuery] string? storageKey, [FromQuery] bool force, CancellationToken cancellationToken)
    {
        var (error, channel) = await channelAudio.ResolveAsync(
            accountId, storageKey, createIfMissing: true, retryFailedAndPending: true, forceReencode: force, cancellationToken);
        return error ?? Accepted(channel);
    }

    [HttpGet("encoded-status")]
    [Endpoint(EndpointIds.ChannelAudioEncodedStatusList)]
    [EndpointSummary("List channel media with durable audio-encoded status")]
    [EndpointDescription("Returns a paginated, per-media-item true/false encoded flag for every item in the channel, sourced from the durable media.audio_encoding_status table rather than job/rendition history. Includes the channel-wide encoded count for progress reporting regardless of the current page or filter.")]
    public async Task<IActionResult> GetEncodedStatus(
        long accountId,
        [FromQuery] bool? isEncoded,
        [FromQuery] string? storageKey,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ListChannelEncodingStatusResponse? response;
        try
        {
            response = await messageBus.RequestAsync<ListChannelEncodingStatusRequest, ListChannelEncodingStatusResponse>(
                AudioEncodingStatusSubjects.ListChannel,
                new ListChannelEncodingStatusRequest { AccountId = accountId, IsEncoded = isEncoded, StorageKey = storageKey, Limit = limit, Cursor = cursor },
                EncodingStatusTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing audio encoding status for account {AccountId}.", accountId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "DataBridge is unreachable.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response is null || !response.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = response?.ErrorMessage ?? "Audio encoding status query failed.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        return Ok(response);
    }

    [HttpPatch("{mediaGuid:guid}/encoded-status")]
    [Endpoint(EndpointIds.ChannelAudioEncodedStatusSet)]
    [EndpointSummary("Set a media item's durable audio-encoded status")]
    [EndpointDescription("Directly writes the media.audio_encoding_status row for one media item in this channel, independent of the encode job queue. Intended for backfill/correction; the encode pipeline already keeps this in sync automatically on completion.")]
    public async Task<IActionResult> SetEncodedStatus(
        long accountId,
        Guid mediaGuid,
        [FromBody] SetEncodedStatusRequest request,
        CancellationToken cancellationToken)
    {
        SetMediaEncodedStatusResponse? response;
        try
        {
            response = await messageBus.RequestAsync<SetMediaEncodedStatusRequest, SetMediaEncodedStatusResponse>(
                AudioEncodingStatusSubjects.Set,
                new SetMediaEncodedStatusRequest
                {
                    AccountId = accountId,
                    MediaGuid = mediaGuid,
                    IsEncoded = request.IsEncoded,
                    StorageKey = request.StorageKey,
                    StoragePath = request.StoragePath
                },
                EncodingStatusTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed setting audio encoding status for media {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "DataBridge is unreachable.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "DataBridge is unreachable.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (!response.Success)
        {
            return response.ErrorCode == "not_found"
                ? NotFound(new ProblemDetails { Title = response.ErrorMessage, Status = StatusCodes.Status404NotFound })
                : BadRequest(new ProblemDetails { Title = response.ErrorMessage, Status = StatusCodes.Status400BadRequest });
        }

        return Ok(response.Item);
    }

    [HttpPost("podcast-token")]
    [Endpoint(EndpointIds.ChannelAudioPodcastToken)]
    [EndpointSummary("Create a podcast subscription URL")]
    [EndpointDescription("Issues a long-lived signed token scoped to this channel and returns an absolute RSS subscription URL that podcast applications can refresh and use for authenticated Opus enclosure downloads without a browser session.")]
    public async Task<IActionResult> CreatePodcastToken(long accountId, CancellationToken cancellationToken)
    {
        var (error, _) = await channelAudio.ResolveAsync(
            accountId, storageKey: null, createIfMissing: true, retryFailedAndPending: false, forceReencode: false, cancellationToken);
        if (error is not null)
            return error;

        var (token, expiresAt) = podcastTokens.Issue(User, accountId);
        return Ok(new
        {
            feedUrl = BuildPodcastUrl(accountId, token),
            expiresAt
        });
    }

    [HttpGet("podcast.rss")]
    [HttpHead("podcast.rss")]
    [EnableCors(MediaCors.Policy)]
    [Endpoint(EndpointIds.ChannelAudioPodcastFeed)]
    [EndpointSummary("Get the channel audio podcast feed")]
    [EndpointDescription("Returns an RSS 2.0 audio-only podcast feed containing every ready and watch-authorized Opus rendition in the channel. Enclosure URLs preserve the channel-scoped podcast token so normal podcast clients can stream or download episodes.")]
    public async Task<IActionResult> GetPodcast(long accountId, CancellationToken cancellationToken)
    {
        var (error, channel) = await channelAudio.ResolveAsync(
            accountId, storageKey: null, createIfMissing: true, retryFailedAndPending: false, forceReencode: false, cancellationToken);
        if (error is not null)
            return error;

        var ready = channel!.Items
            .Where(x => x.Rendition is { Status: AudioRenditionStatus.Ready, StoragePath: not null })
            .ToArray();
        var accessible = await FilterAccessibleAsync(ready, cancellationToken);
        var token = Request.Query[PodcastTokenDefaults.QueryParameter].ToString();
        var bytes = BuildRss(channel, accessible, string.IsNullOrWhiteSpace(token) ? null : token);
        Response.Headers.CacheControl = "private, no-store";
        return File(bytes, "application/rss+xml; charset=utf-8");
    }

    [HttpGet("episodes/{mediaGuid:guid}.opus")]
    [HttpHead("episodes/{mediaGuid:guid}.opus")]
    [EnableCors(MediaCors.Policy)]
    [Endpoint(EndpointIds.ChannelAudioEnclosure)]
    [EndpointSummary("Stream a channel podcast episode")]
    [EndpointDescription("Streams one ready Opus rendition as a range-enabled podcast enclosure after verifying that the media belongs to the requested channel and that the token or signed-in user still has watch access to it. If the blob is actually missing from storage despite the rendition being marked ready, the durable encoded-status flag is reset to false so the channel page reflects reality again.")]
    public async Task<IActionResult> GetEpisode(
        long accountId,
        Guid mediaGuid,
        CancellationToken cancellationToken)
    {
        var (error, channel) = await channelAudio.ResolveAsync(
            accountId, storageKey: null, createIfMissing: false, retryFailedAndPending: false, forceReencode: false, cancellationToken);
        if (error is not null)
            return error;

        var item = channel!.Items.FirstOrDefault(x => x.MediaGuid == mediaGuid);
        if (item?.Rendition is not { Status: AudioRenditionStatus.Ready, StoragePath: not null } rendition)
            return NotFound("This channel episode is not ready.");

        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
            return denied;

        var result = await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            rendition.StorageKey,
            rendition.StoragePath,
            subject: "podcast episode",
            contentType: AudioRenditionHelpers.ContentType,
            cancellationToken: cancellationToken);

        if (result is NotFoundObjectResult)
            await MarkNotActuallyEncodedAsync(accountId, mediaGuid, cancellationToken);

        return result;
    }

    // The rendition row says Ready, but the blob it points at is gone (deleted out-of-band, storage
    // reconfigured, etc.) — self-heal the durable flag rather than leaving it stuck reporting encoded.
    private async Task MarkNotActuallyEncodedAsync(long accountId, Guid mediaGuid, CancellationToken cancellationToken)
    {
        try
        {
            await messageBus.RequestAsync<SetMediaEncodedStatusRequest, SetMediaEncodedStatusResponse>(
                AudioEncodingStatusSubjects.Set,
                new SetMediaEncodedStatusRequest { AccountId = accountId, MediaGuid = mediaGuid, IsEncoded = false },
                EncodingStatusTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed resetting audio encoding status after a missing podcast episode blob for {MediaGuid}.", mediaGuid);
        }
    }

    private async Task<IReadOnlyList<ChannelAudioItemDto>> FilterAccessibleAsync(
        IReadOnlyList<ChannelAudioItemDto> items,
        CancellationToken cancellationToken)
    {
        var allowed = new ConcurrentDictionary<Guid, bool>();
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                allowed[item.MediaGuid] = await accessChecker.CheckWatchAccessAsync(User, item.MediaGuid, ct) is null;
            });
        return items.Where(x => allowed.GetValueOrDefault(x.MediaGuid)).ToArray();
    }

    private byte[] BuildRss(
        ChannelAudioDto channel,
        IReadOnlyList<ChannelAudioItemDto> items,
        string? token)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
               {
                   Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   Indent = true
               }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("rss");
            writer.WriteAttributeString("version", "2.0");
            writer.WriteStartElement("channel");
            writer.WriteElementString("title", channel.AccountName);
            writer.WriteElementString("link", PublicOrigin());
            writer.WriteElementString(
                "description",
                string.IsNullOrWhiteSpace(channel.AccountDescription)
                    ? $"Audio editions of archived videos from {channel.AccountName}."
                    : channel.AccountDescription);
            writer.WriteElementString("language", "en");
            writer.WriteElementString("generator", "FrostStream");
            writer.WriteElementString("lastBuildDate", SystemClock.Instance.GetCurrentInstant()
                .ToDateTimeUtc().ToString("R", CultureInfo.InvariantCulture));

            foreach (var item in items)
            {
                var rendition = item.Rendition!;
                var episodeUrl = BuildEpisodeUrl(channel.AccountId, item.MediaGuid, token);
                writer.WriteStartElement("item");
                writer.WriteElementString("title", item.Title);
                writer.WriteElementString("description", item.Description ?? item.Title);
                writer.WriteElementString("guid", $"urn:uuid:{item.MediaGuid:D}");
                if (item.ReleaseDate is { } releaseDate)
                {
                    writer.WriteElementString(
                        "pubDate",
                        releaseDate.ToDateTimeUtc().ToString("R", CultureInfo.InvariantCulture));
                }
                writer.WriteStartElement("enclosure");
                writer.WriteAttributeString("url", episodeUrl);
                writer.WriteAttributeString("length", (rendition.SizeBytes ?? 0).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("type", AudioRenditionHelpers.ContentType);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    private string BuildPodcastUrl(long accountId, string token)
        => $"{PublicOrigin()}/api/media/channels/{accountId}/audio/podcast.rss?{PodcastTokenDefaults.QueryParameter}={Uri.EscapeDataString(token)}";

    private string BuildEpisodeUrl(long accountId, Guid mediaGuid, string? token)
    {
        var url = $"{PublicOrigin()}/api/media/channels/{accountId}/audio/episodes/{mediaGuid:D}.opus";
        return string.IsNullOrWhiteSpace(token)
            ? url
            : $"{url}?{PodcastTokenDefaults.QueryParameter}={Uri.EscapeDataString(token)}";
    }

    private string PublicOrigin()
        => authOptions.Value.PublicOrigin.TrimEnd('/');
}

public sealed record SetEncodedStatusRequest
{
    public required bool IsEncoded { get; init; }
    public string? StorageKey { get; init; }
    public string? StoragePath { get; init; }
}
