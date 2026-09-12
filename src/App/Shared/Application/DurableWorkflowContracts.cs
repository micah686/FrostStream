using Shared.Backups;
using Shared.Messaging;

namespace Shared.Application;

public enum DurableWorkDisposition
{
    Accepted,
    Duplicate,
    PersistedButPaused
}

/// <summary>The durable acceptance result returned only after authoritative state is committed.</summary>
public sealed record DurableWorkReceipt(
    DurableWorkDisposition Disposition,
    Guid WorkId,
    string DeduplicationKey);

public enum WorkExecutionDisposition
{
    Completed,
    AlreadyCompleted,
    Retry,
    Cancelled,
    Failed
}

/// <summary>Transport-independent executor outcome; adapters decide whether and how to acknowledge it.</summary>
public sealed record WorkExecutionResult(
    WorkExecutionDisposition Disposition,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    TimeSpan? RetryAfter = null);

public interface IDurableWorkflowIngress<in TRequest>
{
    Task<DurableWorkReceipt> AcceptAsync(TRequest request, CancellationToken cancellationToken = default);
}

public interface ILocalWorkExecutor<in TWork>
{
    Task<WorkExecutionResult> ExecuteAsync(TWork work, CancellationToken cancellationToken = default);
}

/// <summary>Persists download intent before a transport or direct caller treats it as accepted.</summary>
public interface IDownloadWorkflowIngress : IDurableWorkflowIngress<DownloadRequested>,
    IDurableWorkflowIngress<DownloadGroupRequested>;

public interface IDownloadWorkExecutor : ILocalWorkExecutor<IFlowMessage>;

public interface ICreatorScanExecutor :
    ILocalWorkExecutor<ChannelScanRefreshRequested>,
    ILocalWorkExecutor<ChannelScanFullRequested>,
    ILocalWorkExecutor<ChannelAssetRefreshRequested>;

public interface IPlaylistExpansionExecutor : ILocalWorkExecutor<IPlaylistFlowMessage>;

public interface IImportWorkExecutor : ILocalWorkExecutor<IFlowMessage>;

public interface IMediaProcessingExecutor :
    ILocalWorkExecutor<AudioRenditionEncodeRequested>,
    ILocalWorkExecutor<StreamRenditionEncodeRequested>,
    ILocalWorkExecutor<GenerateMissingMediaThumbnailsRequested>;

public interface ISearchAndChatWorkExecutor :
    ILocalWorkExecutor<SearchReindexRequested>,
    ILocalWorkExecutor<LiveChatIngestRequested>,
    ILocalWorkExecutor<LiveChatBackfillRequested>;

public interface IScheduledWorkDispatcher : IDurableWorkflowIngress<ScheduledBackgroundRequest>;

public interface IBackupWorkExecutor :
    ILocalWorkExecutor<CreateBackupJobRequest>,
    ILocalWorkExecutor<VerifyBackupRequest>;

/// <summary>Reconciles durable artifact facts with external bytes after retry or process loss.</summary>
public interface IArtifactReconciler<in TWork>
{
    Task ReconcileAsync(TWork work, CancellationToken cancellationToken = default);
}
