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
    IBlobStorageProvider blobStorageProvider,
    MediaAccessChecker accessChecker,
    ILogger<ChannelAudioController> logger) : ControllerBase
{
    [HttpGet("status")]
    [Endpoint(EndpointIds.ChannelAudioStatus)]
    [EndpointSummary("Get channel audio encoding progress")]
    [EndpointDescription("Returns aggregate missing, queued, processing, ready, and failed Opus rendition counts for every archived item in a channel, together with the ordered ready-item metadata used by the virtual audio playlist.")]
    public async Task<IActionResult> GetStatus(long accountId, CancellationToken cancellationToken)
    {
        var (error, channel) = await channelAudio.ResolveAsync(
            accountId, createIfMissing: false, retryFailedAndPending: false, cancellationToken);
        return error ?? Ok(channel);
    }

    [HttpPost("encode")]
    [Endpoint(EndpointIds.ChannelAudioEncode)]
    [EndpointSummary("Encode a channel as Opus audio")]
    [EndpointDescription("Creates any missing Opus audio rendition records for archived items in the channel, retries failed renditions, and publishes idempotent MediaProcessor jobs for everything still waiting to be encoded.")]
    public async Task<IActionResult> Encode(long accountId, CancellationToken cancellationToken)
    {
        var (error, channel) = await channelAudio.ResolveAsync(
            accountId, createIfMissing: true, retryFailedAndPending: true, cancellationToken);
        return error ?? Accepted(channel);
    }

    [HttpPost("podcast-token")]
    [Endpoint(EndpointIds.ChannelAudioPodcastToken)]
    [EndpointSummary("Create a podcast subscription URL")]
    [EndpointDescription("Issues a long-lived signed token scoped to this channel and returns an absolute RSS subscription URL that podcast applications can refresh and use for authenticated Opus enclosure downloads without a browser session.")]
    public async Task<IActionResult> CreatePodcastToken(long accountId, CancellationToken cancellationToken)
    {
        var (error, _) = await channelAudio.ResolveAsync(
            accountId, createIfMissing: true, retryFailedAndPending: false, cancellationToken);
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
            accountId, createIfMissing: true, retryFailedAndPending: false, cancellationToken);
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
    [EndpointDescription("Streams one ready Opus rendition as a range-enabled podcast enclosure after verifying that the media belongs to the requested channel and that the token or signed-in user still has watch access to it.")]
    public async Task<IActionResult> GetEpisode(
        long accountId,
        Guid mediaGuid,
        CancellationToken cancellationToken)
    {
        var (error, channel) = await channelAudio.ResolveAsync(
            accountId, createIfMissing: false, retryFailedAndPending: false, cancellationToken);
        if (error is not null)
            return error;

        var item = channel!.Items.FirstOrDefault(x => x.MediaGuid == mediaGuid);
        if (item?.Rendition is not { Status: AudioRenditionStatus.Ready, StoragePath: not null } rendition)
            return NotFound("This channel episode is not ready.");

        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
            return denied;

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            rendition.StorageKey,
            rendition.StoragePath,
            subject: "podcast episode",
            contentType: AudioRenditionHelpers.ContentType,
            cancellationToken: cancellationToken);
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
