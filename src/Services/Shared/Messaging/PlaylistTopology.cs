using FlySwattr.NATS.Abstractions;

namespace Shared.Messaging;

/// <summary>
/// JetStream topology for the playlist pipeline. One stream (<c>FROSTSTREAM_PLAYLIST</c>)
/// covers every <c>playlist.&gt;</c> subject (excluding the request/reply NATS-Core query
/// subjects which don't go through JetStream). Durable consumers exist per ingress path.
///
/// Lives in <c>Shared</c> so DataBridge and Worker can both register it via
/// <c>AddNatsTopologySource&lt;PlaylistTopology&gt;()</c>.
/// </summary>
public sealed class PlaylistTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_PLAYLIST";

    /// <summary>
    /// Subject filter for the JetStream stream. Excludes the <c>playlist.get</c> /
    /// <c>playlist.list</c> request/reply subjects which are handled over core NATS.
    /// </summary>
    public const string SubjectFilter = "playlist.>";

    public const string DataBridgeQueueGroup = "databridge-playlists";
    public const string WorkerQueueGroup = "workers";

    public const string PlaylistRequestedConsumer            = "databridge-playlist-requested";
    public const string PlaylistMetadataFetchedConsumer      = "databridge-playlist-metadata-fetched";
    public const string PlaylistMetadataFetchFailedConsumer  = "databridge-playlist-metadata-fetch-failed";
    public const string ProcessStagedEntriesConsumer         = "databridge-playlist-process-staged-entries";

    public const string WorkerFetchPlaylistMetadataConsumer  = "worker-fetch-playlist-metadata";

    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec
        {
            Name = StreamName.From(StreamNameValue),
            Subjects = [SubjectFilter],
            MaxAge = TimeSpan.FromDays(30),
            RetentionPolicy = StreamRetention.Limits,
            StorageType = StorageType.File,
            Replicas = 1
        };
    }

    public IEnumerable<ConsumerSpec> GetConsumers()
    {
        yield return DataBridgeConsumer(PlaylistRequestedConsumer,           PlaylistSubjects.PlaylistRequested);
        yield return DataBridgeConsumer(PlaylistMetadataFetchedConsumer,     PlaylistSubjects.PlaylistMetadataFetched);
        yield return DataBridgeConsumer(PlaylistMetadataFetchFailedConsumer, PlaylistSubjects.PlaylistMetadataFetchFailed);
        yield return DataBridgeConsumer(ProcessStagedEntriesConsumer,        PlaylistSubjects.ProcessPlaylistStagedEntriesCommand);

        yield return WorkerConsumer(WorkerFetchPlaylistMetadataConsumer,     PlaylistSubjects.FetchPlaylistMetadataCommand);
    }

    private static ConsumerSpec DataBridgeConsumer(string durableName, string subject)
        => Consumer(durableName, subject, DataBridgeQueueGroup);

    private static ConsumerSpec WorkerConsumer(string durableName, string subject)
        => Consumer(durableName, subject, WorkerQueueGroup);

    private static ConsumerSpec Consumer(string durableName, string subject, string queueGroup)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(durableName),
            DeliverGroup = QueueGroup.From(queueGroup),
            FilterSubject = subject,
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(2),
            MaxDeliver = 10
        };
}
