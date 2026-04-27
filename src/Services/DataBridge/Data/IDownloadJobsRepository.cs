using Shared.Messaging;

namespace DataBridge.Data;

/// <summary>
/// Persistence façade for the download orchestration. Scoped — resolve through
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/> from singletons
/// (e.g. ingress hosted services and the Cleipnir flow).
/// </summary>
public interface IDownloadJobsRepository
{
    Task<bool> IsMessageProcessedAsync(Guid messageId, CancellationToken ct = default);

    Task MarkMessageProcessedAsync(Guid messageId, string operationKey, Guid jobId, CancellationToken ct = default);

    Task CreateJobIfMissingAsync(DownloadRequested request, CancellationToken ct = default);

    Task UpdateStateAsync(Guid jobId, DownloadJobState state, CancellationToken ct = default);

    Task ApplyMetadataAsync(Guid jobId, MetadataFetched evt, CancellationToken ct = default);

    Task ApplyDownloadCompletedAsync(Guid jobId, DownloadCompleted evt, CancellationToken ct = default);

    Task CommitUploadAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default);

    Task IncrementMetadataAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default);

    Task IncrementDownloadAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default);

    Task IncrementUploadAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default);

    Task RecordHistoryAsync(Guid jobId, Guid messageId, string operationKey, string eventName, string? payloadJson, CancellationToken ct = default);

    Task RecordTerminalFailureAsync(Guid jobId, FailureKind kind, string? code, string message, DownloadJobState terminalState, string? lastPayloadJson, CancellationToken ct = default);
}
