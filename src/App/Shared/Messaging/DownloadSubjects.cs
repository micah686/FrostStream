namespace Shared.Messaging;

/// <summary>Download Flow V2 subjects. No V1 aliases are provisioned.</summary>
public static class DownloadSubjects
{
    public const string GroupRequested = "download.v2.request.group";
    public const string DownloadRequested = "download.v2.request.job";

    public const string ExpandGroupCommand = "download.v2.command.group.expand";
    public const string FetchMetadataCommand = "download.v2.command.metadata.fetch";
    public const string DownloadVideoCommand = "download.v2.command.media.acquire";

    public static string Tagged(string baseSubject, string tag) => $"{baseSubject}.{tag}";
    public static string FetchMetadataCommandForTag(string tag) => Tagged(FetchMetadataCommand, tag);
    public static string DownloadVideoCommandForTag(string tag) => Tagged(DownloadVideoCommand, tag);

    public const string MetadataFetched = "download.v2.event.metadata.succeeded";
    public const string MetadataFetchFailed = "download.v2.event.metadata.failed";
    public const string DownloadCompleted = "download.v2.event.media.succeeded";
    public const string DownloadFailed = "download.v2.event.media.failed";
    public const string DownloadProgress = "download.v2.progress";

    public const string StageStarted = "download.v2.event.stage.started";
    public const string StageHeartbeat = "download.v2.event.stage.heartbeat";
    public const string StageSucceeded = "download.v2.event.stage.succeeded";
    public const string StageFailed = "download.v2.event.stage.failed";
    public const string StageStopped = "download.v2.event.stage.stopped";
    public const string GroupExpansionSucceeded = "download.v2.event.group.expansion.succeeded";
    public const string GroupExpansionFailed = "download.v2.event.group.expansion.failed";

    // Core NATS request/reply controls.
    public const string UpdatePriorityRequest = "download.v2.control.job.priority";
    public const string StartDownloadRequest = "download.v2.control.job.start";
    public const string StopDownloadRequest = "download.v2.control.job.stop";
    public const string StartGroupRequest = "download.v2.control.group.start";
    public const string StopGroupRequest = "download.v2.control.group.stop";
    public const string ClearProviderCircuitRequest = "download.v2.control.provider.clear";
    public const string AcquireLeaseRequest = "download.v2.control.lease.acquire";
    public const string RenewLeaseRequest = "download.v2.control.lease.renew";
    public const string StopActiveRun = "download.v2.control.worker.stop";

}
