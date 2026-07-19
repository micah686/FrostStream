using NodaTime;
using Shared.Messaging;

namespace DataBridge.Data;

public interface IDownloadFlowV2Repository
{
    Task<DownloadRunRequest?> CreateInitialRunAsync(DownloadRequested request, bool autoStart, CancellationToken ct = default);
    Task<DownloadRunRequest?> StartFreshRunAsync(Guid jobId, CancellationToken ct = default);
    Task<DownloadControlDecision> RequestStopAsync(Guid jobId, string? requestedBy, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadRunRequest>> StartGroupAsync(Guid correlationId, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadControlDecision>> StopGroupAsync(Guid correlationId, string? requestedBy, string? reason, CancellationToken ct = default);

    Task<bool> BeginStageAttemptAsync(DownloadExecutionIdentity execution, string operationKey, CancellationToken ct = default);
    Task<bool> CompleteStageAttemptAsync(DownloadExecutionIdentity execution, CancellationToken ct = default);
    Task<bool> MarkRetryWaitingAsync(DownloadExecutionIdentity execution, FailureKind kind, string? code, string message, CancellationToken ct = default);
    Task<bool> FailStageAttemptAsync(DownloadExecutionIdentity execution, FailureKind kind, string? code,
        string message, CancellationToken ct = default);
    Task<bool> TransitionAsync(Guid jobId, Guid runId, DownloadJobStatus status, DownloadStage stage,
        DownloadStageStatus stageStatus, int attempt = 0, string? artifactKey = null, CancellationToken ct = default);
    Task<bool> FailRunAsync(Guid jobId, Guid runId, FailureKind kind, string? code, string message, CancellationToken ct = default);
    Task<bool> CompleteRunAsync(Guid jobId, Guid runId, bool withWarnings, CancellationToken ct = default);
    Task<bool> FinalizeRunAsync(DownloadExecutionIdentity execution, Guid mediaGuid,
        string? provider = null, string? sourceMediaId = null, Instant? sourceLastModified = null,
        CancellationToken ct = default);
    Task<bool> MarkStoppedAsync(Guid jobId, Guid runId, string? message, CancellationToken ct = default);
    Task<bool> MarkAlreadyDownloadedAsync(Guid jobId, Guid runId, Guid mediaGuid,
        string? provider, string? sourceMediaId, Instant? sourceLastModified = null,
        CancellationToken ct = default);
    Task<bool> IsStopRequestedAsync(Guid jobId, Guid runId, CancellationToken ct = default);
    Task RecordWarningAsync(Guid jobId, Guid runId, DownloadStage stage, string? artifactKey,
        string code, string message, CancellationToken ct = default);
    Task UpsertArtifactAsync(DownloadArtifactSnapshot artifact, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadArtifactSnapshot>> ListCompensatableArtifactsAsync(Guid jobId, Guid runId,
        CancellationToken ct = default);

    Task<AcquireDownloadLeaseResponse> TryAcquireLeaseAsync(AcquireDownloadLeaseRequest request, CancellationToken ct = default);
    Task<RenewDownloadLeaseResponse> TryRenewLeaseAsync(RenewDownloadLeaseRequest request, CancellationToken ct = default);
    Task ReleaseLeaseAsync(Guid dispatchId, DownloadWorkerLeaseStatus status, CancellationToken ct = default);
    Task<bool> CanAcceptWorkerEventAsync(DownloadExecutionIdentity execution, CancellationToken ct = default);
    Task<IReadOnlyList<ExpiredDownloadLease>> FailExpiredLeasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ActiveDownloadRun>> ListActiveRunsAsync(Duration minAge, CancellationToken ct = default);
    Task<StartupReconciliationResult> ReconcileForStartupAsync(CancellationToken ct = default);

    Task CreateGroupIfMissingAsync(DownloadGroupRequested request, CancellationToken ct = default);
    Task SetGroupStatusAsync(Guid groupId, DownloadGroupStatus status, string? failureCode = null,
        string? failureMessage = null, CancellationToken ct = default);
    Task RefreshGroupAggregateAsync(Guid correlationId, CancellationToken ct = default);
    Task<bool> IsGroupExpansionAllowedAsync(Guid correlationId, CancellationToken ct = default);
    Task<bool> CanAcceptGroupChildAsync(Guid correlationId, CancellationToken ct = default);
    Task OpenProviderCircuitAsync(string provider, string reason, CancellationToken ct = default);
    Task ClearProviderCircuitAsync(string provider, CancellationToken ct = default);
    Task<string?> FindOpenProviderCircuitAsync(string sourceUrl, CancellationToken ct = default);
}

public sealed record DownloadControlDecision(
    bool Accepted,
    bool Found,
    Guid JobId,
    Guid? RunId,
    Guid CorrelationId,
    DownloadJobStatus? Status,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record DownloadArtifactSnapshot
{
    public required Guid JobId { get; init; }
    public required Guid RunId { get; init; }
    public required DownloadStage Stage { get; init; }
    public required string ArtifactKey { get; init; }
    public required UploadArtifactKind Kind { get; init; }
    public required bool Required { get; init; }
    public required DownloadArtifactStatus Status { get; init; }
    public string? TempFileRef { get; init; }
    public string? StorageKey { get; init; }
    public string? StoragePath { get; init; }
    public string? StorageVersion { get; init; }
    public string? ContentHashXxh128 { get; init; }
    public long? SizeBytes { get; init; }
    public string? WarningCode { get; init; }
    public string? WarningMessage { get; init; }
}

public sealed record StartupReconciliationResult(
    int StoppedQueuedJobs,
    int FailedActiveJobs,
    int ExpiredLeases,
    int FailedActiveGroups);

public sealed record ExpiredDownloadLease(Guid JobId, Guid RunId, Guid DispatchId);

public sealed record ActiveDownloadRun(Guid JobId, Guid RunId);
