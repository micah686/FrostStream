using NodaTime;
using Shared.Messaging;
using Shared.Metadata;

namespace DataBridge.Data;

public interface IImportSessionRepository
{
    Task<ImportSessionDto> CreateAsync(ImportSessionCreateRequest request, Guid sessionId, Guid correlationId, CancellationToken ct = default);

    Task<IReadOnlyList<ImportSessionDto>> ListAsync(ImportSessionListRequest request, CancellationToken ct = default);

    Task<ImportSessionDto?> GetAsync(Guid sessionId, CancellationToken ct = default);

    Task<(IReadOnlyList<ImportSessionItemDto> Items, Guid? NextItemId, int TotalCount)> ListItemsAsync(
        ImportSessionItemsListRequest request,
        CancellationToken ct = default);

    Task<ImportSessionDto?> IngestScannedItemsAsync(
        Guid sessionId,
        IReadOnlyList<ImportSessionScannedItem> items,
        CancellationToken ct = default);

    Task<ImportSessionDto?> MarkScanFailedAsync(Guid sessionId, string errorMessage, CancellationToken ct = default);

    Task<IReadOnlyList<ImportSessionProbeItemRef>> ListItemsForProbeAsync(Guid sessionId, int batchSize, CancellationToken ct = default);

    Task<ImportSessionDto?> ApplyProbeResultsAsync(Guid sessionId, IReadOnlyList<ImportSessionProbeResult> results, CancellationToken ct = default);

    Task<ImportSessionDto?> ApplyProbeFailuresAsync(Guid sessionId, IReadOnlyList<ImportSessionProbeFailure> failures, CancellationToken ct = default);

    Task<(ImportSessionItemDto? Item, ImportSessionDto? Session)> PatchItemAsync(
        ImportSessionItemPatchRequest request,
        CancellationToken ct = default);

    Task<(int AffectedCount, ImportSessionDto? Session)> ApplyBulkAsync(
        ImportSessionItemsBulkRequest request,
        CancellationToken ct = default);

    Task<(int MatchedCount, int UnmatchedCount, ImportSessionDto? Session)> ApplyMappingAsync(
        Guid sessionId,
        IReadOnlyList<ImportSessionMappingRow> rows,
        string objectBucket,
        string objectKey,
        string format,
        CancellationToken ct = default);

    Task<IReadOnlyList<ImportSessionMappingTemplateRow>> ListMappingTemplateAsync(
        Guid sessionId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ImportSessionEnrichItemRef>> ListItemsForEnrichAsync(
        Guid sessionId,
        IReadOnlyList<Guid>? itemIds,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<ImportSessionMetadataRefreshItemRef>> ListItemsForMetadataRefreshAsync(
        Guid sessionId,
        IReadOnlyList<Guid>? itemIds,
        int limit,
        CancellationToken ct = default);

    Task<ImportSessionDto?> MarkEnrichmentQueuedAsync(
        Guid sessionId,
        IReadOnlyList<Guid> itemIds,
        CancellationToken ct = default);

    Task<ImportSessionDto?> ApplyEnrichmentAsync(ImportSessionItemEnriched message, CancellationToken ct = default);

    Task<ImportSessionDto?> ApplyEnrichFailureAsync(ImportSessionItemEnrichFailed message, CancellationToken ct = default);

    Task<(ImportSessionDto? Session, string? Error)> UpdateOptionsAsync(ImportSessionUpdateOptionsRequest request, CancellationToken ct = default);

    Task<(ImportSessionDto? Session, int ApprovedCount, string? Error)> CommitAsync(Guid sessionId, CancellationToken ct = default);

    Task<(ImportSessionDto? Session, int ResetCount)> RetryFailedAsync(Guid sessionId, CancellationToken ct = default);

    Task<ImportSessionDto?> CancelAsync(Guid sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<ImportSessionDto>> ListCommittingSessionsAsync(int limit, CancellationToken ct = default);

    Task<int> RecoverStaleHashingItemsAsync(Guid sessionId, Instant staleBefore, CancellationToken ct = default);

    Task<IReadOnlyList<ImportSessionItemWork>> ClaimApprovedWorkAsync(Guid sessionId, int limit, CancellationToken ct = default);

    Task<ImportSessionItemWork?> GetItemWorkAsync(Guid sessionId, Guid itemId, CancellationToken ct = default);

    Task MarkItemHashingAsync(Guid sessionId, Guid itemId, CancellationToken ct = default);

    Task MarkItemPreparedAsync(Guid sessionId, Guid itemId, LocalImportFilePrepared prepared, CancellationToken ct = default);

    Task MarkItemUploadingAsync(Guid sessionId, Guid itemId, Guid mediaGuid, string storagePath, CancellationToken ct = default);

    Task MarkItemFinalizingAsync(Guid sessionId, Guid itemId, CancellationToken ct = default);

    Task MarkItemAlreadyImportedAsync(Guid sessionId, Guid itemId, Guid mediaGuid, string storagePath, LocalImportFilePrepared prepared, CancellationToken ct = default);

    Task MarkItemImportedAsync(
        Guid sessionId,
        Guid itemId,
        Guid mediaGuid,
        string storagePath,
        string? storageVersion,
        string? metaStoragePath,
        string? infoJsonStoragePath,
        string? thumbnailStoragePath,
        string? captionStoragePathsJson,
        CancellationToken ct = default);

    Task MarkItemCommitFailedAsync(
        Guid sessionId,
        Guid itemId,
        string? errorCode,
        string errorMessage,
        Guid? mediaGuid = null,
        string? storagePath = null,
        CancellationToken ct = default);

    Task<ImportSessionDto?> CompleteSessionIfTerminalAsync(Guid sessionId, CancellationToken ct = default);
}

public sealed record ImportSessionMappingRow : ImportSessionUserMetadata
{
    public required string FileName { get; init; }
    public CapturedMediaMetadata? Metadata { get; init; }
}

public sealed record ImportSessionEnrichItemRef
{
    public required Guid ItemId { get; init; }
    public required string SourceUrl { get; init; }
    public required string RelativePath { get; init; }
    public int Attempt { get; init; }
    public string? Provider { get; init; }
}

public sealed record ImportSessionMetadataRefreshItemRef
{
    public required Guid ItemId { get; init; }
    public required string RelativePath { get; init; }
    public int Attempt { get; init; }
    public string? Provider { get; init; }
    public string? SourceUrl { get; init; }
}

public sealed record ImportSessionItemWork
{
    public required Guid SessionId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid ItemId { get; init; }
    public required string StorageKey { get; init; }
    public string? WorkerTag { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public string? SidecarsJson { get; init; }
    public string? Provider { get; init; }
    public string? SourceMediaId { get; init; }
    public NodaTime.Instant? SourceLastModified { get; init; }
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public string? ProbeMetadataJson { get; init; }
    public string? ScanMetadataJson { get; init; }
    public string? EnrichedMetadataJson { get; init; }
    public string? UserMetadataJson { get; init; }
    public ImportSessionItemMetadataState MetadataState { get; init; }
    public int Attempt { get; init; }
    public bool DeleteSourceFiles { get; init; }
}
