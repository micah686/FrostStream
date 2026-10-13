
namespace Shared.Messaging;

/// <summary>Fresh Download Flow V2 topology. Artifact transfer has its own stream.</summary>
public static class DownloadTopology
{
    public const string StreamNameValue = "FROSTSTREAM_DOWNLOAD_V2";

    /// <summary>
    /// Durable traffic only. The control-plane request/reply subjects
    /// (<c>download.v2.control.&gt;</c> — job start/stop, lease acquire/renew, …) must never be
    /// captured by the stream: JetStream sends its publish ack to the request's reply inbox,
    /// which the requester then mistakes for the responder's reply (see PlaylistTopology).
    /// </summary>
    public static readonly string[] SubjectFilters =
    [
        "download.v2.request.>",
        "download.v2.command.>",
        "download.v2.event.>",
        DownloadSubjects.DownloadProgress
    ];

    public const string GroupRequestedConsumer = "databridge-download-v2-group-requested";
    public const string DownloadRequestedConsumer = "databridge-download-v2-job-requested";
    public const string MetadataFetchedConsumer = "databridge-download-v2-metadata-succeeded";
    public const string MetadataFetchFailedConsumer = "databridge-download-v2-metadata-failed";
    public const string DownloadCompletedConsumer = "databridge-download-v2-media-succeeded";
    public const string DownloadFailedConsumer = "databridge-download-v2-media-failed";
    public const string StageStartedConsumer = "databridge-download-v2-stage-started";
    public const string StageHeartbeatConsumer = "databridge-download-v2-stage-heartbeat";
    public const string StageSucceededConsumer = "databridge-download-v2-stage-succeeded";
    public const string StageFailedConsumer = "databridge-download-v2-stage-failed";
    public const string StageStoppedConsumer = "databridge-download-v2-stage-stopped";
    public const string GroupExpansionSucceededConsumer = "databridge-download-v2-group-expansion-succeeded";
    public const string GroupExpansionFailedConsumer = "databridge-download-v2-group-expansion-failed";

    public const string WorkerFetchMetadataConsumer = "worker-download-v2-metadata";
    public const string WorkerDownloadVideoConsumer = "worker-download-v2-media";

}
