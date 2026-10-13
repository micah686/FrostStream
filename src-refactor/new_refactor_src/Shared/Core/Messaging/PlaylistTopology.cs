
namespace Shared.Messaging;

/// <summary>
/// JetStream topology for the playlist pipeline. One stream (<c>FROSTSTREAM_PLAYLIST</c>)
/// covers every <c>playlist.&gt;</c> subject (excluding the request/reply NATS-Core query
/// subjects which don't go through JetStream). Durable consumers exist per ingress path.
///
/// Lives in <c>Shared</c> so DataBridge and Worker can both register it via
/// <c>AddNatsTopologySource&lt;PlaylistTopology&gt;()</c>.
/// </summary>
public static class PlaylistTopology
{
    public const string StreamNameValue = "FROSTSTREAM_PLAYLIST";

    /// <summary>
    /// Subject filters for the JetStream stream. Deliberately enumerates only the pipeline
    /// subjects (group-expansion commands + events). The request/reply core-NATS subjects
    /// (<c>playlist.get</c>, <c>playlist.list</c>, <c>playlist.item.force-queue</c>,
    /// <c>playlist.user.&gt;</c>) must never be captured by the stream: JetStream sends its
    /// publish ack to the request's reply inbox, which the requester then mistakes for the
    /// responder's reply.
    /// </summary>
    public static readonly string[] SubjectFilters =
    [
        "playlist.cmd.>",
        "playlist.evt.>"
    ];

    public const string DataBridgeQueueGroup = "databridge-playlists";
    public const string WorkerQueueGroup = "workers";

    public const string PlaylistMetadataFetchedConsumer      = "databridge-playlist-metadata-fetched";
    public const string PlaylistMetadataFetchFailedConsumer  = "databridge-playlist-metadata-fetch-failed";

    public const string WorkerFetchPlaylistMetadataConsumer  = "worker-fetch-playlist-metadata";

}
