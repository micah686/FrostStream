using NodaTime;

namespace Shared.Messaging;

public static class ImportSessionSubjects
{
    public const string Create = "import-session.create";
    public const string List = "import-session.list";
    public const string Get = "import-session.get";
    public const string ItemsList = "import-session.items.list";
    public const string ItemsPatch = "import-session.items.patch";
    public const string ItemsBulk = "import-session.items.bulk";
    public const string MappingApply = "import-session.mapping.apply";
    public const string MappingTemplate = "import-session.mapping.template";
    public const string MetadataRefresh = "import-session.metadata.refresh";
    public const string Enrich = "import-session.enrich";
    public const string UpdateOptions = "import-session.update-options";
    public const string Commit = "import-session.commit";
    public const string RetryFailed = "import-session.retry-failed";
    public const string Cancel = "import-session.cancel";
    public const string ScanIngest = "import-session.scan.ingest";
    public const string ScanFailed = "import-session.scan.failed";
    public const string StateChanged = "import-session.state-changed";
    public const string QueueGroup = "databridge-import-sessions";
}

public enum ImportSessionStatus
{
    Scanning = 0,
    ScanFailed = 1,
    Reviewing = 2,
    Committing = 3,
    Completed = 4,
    CompletedWithFailures = 5,
    Cancelled = 6
}

public enum ImportSessionSourceKind
{
    WorkerIncoming = 0,
    StorageBackend = 1
}

public enum ImportSessionItemStatus
{
    Discovered = 0,
    Probed = 1,
    Approved = 2,
    Hashing = 3,
    Uploading = 4,
    Finalizing = 5,
    Imported = 6,
    AlreadyImported = 7,
    Failed = 8
}

public enum ImportSessionItemMetadataState
{
    Incomplete = 0,
    Ready = 1,
    Edited = 2,
    PlaceholderAccepted = 3
}

public enum ImportSessionItemMetadataSource
{
    Placeholder = 0,
    Nfo = 1,
    InfoJson = 2,
    YtDlp = 3,
    ManualMapping = 4
}

public enum ImportSessionMetadataFetchState
{
    NotAttempted = 0,
    Queued = 1,
    Succeeded = 2,
    Failed = 3
}

public enum ImportSessionBulkAction
{
    AcceptPlaceholders = 0,
    Exclude = 1,
    Include = 2,
    ResetFailed = 3
}

public sealed record ImportSessionCreateRequest
{
    public ImportSessionSourceKind SourceKind { get; init; } = ImportSessionSourceKind.WorkerIncoming;
    public string? StorageKey { get; init; }
    public string? WorkerTag { get; init; }
    public string? SubPath { get; init; }
    public string? RequestedBy { get; init; }
    public int? MaxParallelItems { get; init; }
}

public sealed record ImportSessionCreateResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionListRequest
{
    public ImportSessionStatus? Status { get; init; }
    public int Limit { get; init; } = 50;
    public Guid? AfterSessionId { get; init; }
}

public sealed record ImportSessionListResponse : ImportSessionOperationResponse
{
    public IReadOnlyList<ImportSessionDto> Items { get; init; } = [];
    public Guid? NextSessionId { get; init; }
}

public sealed record ImportSessionGetRequest
{
    public required Guid SessionId { get; init; }
}

public sealed record ImportSessionGetResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionItemsListRequest
{
    public required Guid SessionId { get; init; }
    public ImportSessionItemStatus? Status { get; init; }
    public ImportSessionItemMetadataState? MetadataState { get; init; }
    public string? Search { get; init; }
    public bool? Included { get; init; }
    public Guid? AfterItemId { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record ImportSessionItemsListResponse : ImportSessionOperationResponse
{
    public IReadOnlyList<ImportSessionItemDto> Items { get; init; } = [];
    public Guid? NextItemId { get; init; }
    public int TotalCount { get; init; }
}

public sealed record ImportSessionItemPatchRequest
{
    public required Guid SessionId { get; init; }
    public required Guid ItemId { get; init; }
    public string? Title { get; init; }
    public string? Provider { get; init; }
    public string? SourceMediaId { get; init; }
    public string? SourceUrl { get; init; }
}

public sealed record ImportSessionItemPatchResponse : ImportSessionOperationResponse
{
    public ImportSessionItemDto? Item { get; init; }
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionItemsBulkRequest
{
    public required Guid SessionId { get; init; }
    public required ImportSessionBulkAction Action { get; init; }
    public IReadOnlyList<Guid>? ItemIds { get; init; }
    public ImportSessionItemStatus? Status { get; init; }
    public ImportSessionItemMetadataState? MetadataState { get; init; }
    public string? Search { get; init; }
}

public sealed record ImportSessionItemsBulkResponse : ImportSessionOperationResponse
{
    public int AffectedCount { get; init; }
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionMappingApplyRequest
{
    public required Guid SessionId { get; init; }
    public required string ObjectBucket { get; init; }
    public required string ObjectKey { get; init; }
    public required string Format { get; init; }
}

public sealed record ImportSessionMappingApplyResponse : ImportSessionOperationResponse
{
    public int MatchedCount { get; init; }
    public int UnmatchedCount { get; init; }
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionMappingTemplateRequest
{
    public required Guid SessionId { get; init; }
}

public sealed record ImportSessionMappingTemplateResponse : ImportSessionOperationResponse
{
    public IReadOnlyList<ImportSessionMappingTemplateRow> Items { get; init; } = [];
}

/// <summary>
/// User-supplied metadata overrides for one import item, matching the rich fields
/// <c>CapturedMediaMetadata</c> can persist. Stored as camelCase JSON in
/// <c>import_session_items.user_metadata</c>; blank/missing fields fall through to the
/// info.json / scan-derived values. List fields replace the underlying value entirely when
/// non-empty. <c>ReleaseDate</c> accepts <c>yyyy-MM-dd</c> or <c>yyyyMMdd</c>.
/// </summary>
public record ImportSessionUserMetadata
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Provider { get; init; }
    public string? SourceMediaId { get; init; }
    public string? SourceUrl { get; init; }
    public string? ReleaseDate { get; init; }
    public string? AccountName { get; init; }
    public string? AccountHandle { get; init; }
    public string? AccountUrl { get; init; }
    public string? Location { get; init; }
    public int? AgeLimit { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public IReadOnlyList<string>? Artists { get; init; }
    public IReadOnlyList<string>? AlbumArtists { get; init; }
    public string? Album { get; init; }
    public string? Track { get; init; }
    public int? TrackNumber { get; init; }
    public string? SeriesName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string? EpisodeName { get; init; }
}

public sealed record ImportSessionMappingTemplateRow : ImportSessionUserMetadata
{
    public required string FileName { get; init; }
}

public sealed record ImportSessionEnrichRequest
{
    public required Guid SessionId { get; init; }
    public IReadOnlyList<Guid>? ItemIds { get; init; }
    public ImportSessionYtDlpOptions Options { get; init; } = new();
}

public sealed record ImportSessionMetadataRefreshRequest
{
    public required Guid SessionId { get; init; }
    public IReadOnlyList<Guid>? ItemIds { get; init; }
}

public sealed record ImportSessionMetadataRefreshResponse : ImportSessionOperationResponse
{
    public int CheckedCount { get; init; }
    public int FoundCount { get; init; }
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionYtDlpOptions
{
    public string? ProxyUrl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? TwoFactorCode { get; init; }
    public string? VideoPassword { get; init; }
    public bool SkipCertificateChecks { get; init; }
    public bool AllowLegacyConnections { get; init; }
    public IReadOnlyList<string> ExtraHttpHeaders { get; init; } = [];
    public double SleepBetweenRequestsSeconds { get; init; } = 3;
}

public sealed record ImportSessionEnrichResponse : ImportSessionOperationResponse
{
    public int QueuedCount { get; init; }
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionUpdateOptionsRequest
{
    public required Guid SessionId { get; init; }
    public bool? DeleteSourceFiles { get; init; }
}

public sealed record ImportSessionUpdateOptionsResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionCommitRequest
{
    public required Guid SessionId { get; init; }
}

public sealed record ImportSessionCommitResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
    public int ApprovedCount { get; init; }
}

public sealed record ImportSessionRetryFailedRequest
{
    public required Guid SessionId { get; init; }
}

public sealed record ImportSessionRetryFailedResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
    public int ResetCount { get; init; }
}

public sealed record ImportSessionCancelRequest
{
    public required Guid SessionId { get; init; }
}

public sealed record ImportSessionCancelResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
}

public record ImportSessionOperationResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record ImportSessionDto
{
    public required Guid SessionId { get; init; }
    public Guid CorrelationId { get; init; }
    public ImportSessionStatus Status { get; init; }
    public ImportSessionSourceKind SourceKind { get; init; }
    public required string SourceRoot { get; init; }
    public string? SubPath { get; init; }
    public required string StorageKey { get; init; }
    public string? WorkerTag { get; init; }
    public string? RequestedBy { get; init; }
    public int TotalItems { get; init; }
    public int ProbedItems { get; init; }
    public int ReadyItems { get; init; }
    public int IncompleteItems { get; init; }
    public int ExcludedItems { get; init; }
    public int ApprovedItems { get; init; }
    public int ImportedItems { get; init; }
    public int AlreadyImportedItems { get; init; }
    public int FailedItems { get; init; }
    public int MaxParallelItems { get; init; }
    public bool DeleteSourceFiles { get; init; }
    public string? ErrorMessage { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
    public Instant? CompletedAt { get; init; }
}

public sealed record ImportSessionItemDto
{
    public required Guid ItemId { get; init; }
    public required Guid SessionId { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public Instant? FileMtime { get; init; }
    public string? Provider { get; init; }
    public string? SourceMediaId { get; init; }
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public ImportSessionItemMetadataState MetadataState { get; init; }
    public ImportSessionItemMetadataSource MetadataSource { get; init; }
    public ImportSessionMetadataFetchState MetadataFetchState { get; init; }
    public int MetadataFetchAttempt { get; init; }
    public string? MetadataFetchMessage { get; init; }
    public string? MetadataJson { get; init; }
    public bool HasNfo { get; init; }
    public bool HasInfoJson { get; init; }
    public bool Excluded { get; init; }
    public ImportSessionItemStatus Status { get; init; }
    public int Attempt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
    public Instant? CompletedAt { get; init; }
}

public sealed record ScanLocalImportSourceCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    public required Guid SessionId { get; init; }
    public ImportSessionSourceKind SourceKind { get; init; } = ImportSessionSourceKind.WorkerIncoming;
    public string? SubPath { get; init; }
    public required string StorageKey { get; init; }
    public string? RequiredWorkerTag { get; init; }
}

public sealed record ProbeImportSessionItemsCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    public required Guid SessionId { get; init; }
    public string? RequiredWorkerTag { get; init; }
    public IReadOnlyList<ImportSessionProbeItemRef> Items { get; init; } = [];
}

public sealed record ImportSessionItemImportRequested : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    public required Guid SessionId { get; init; }
    public required Guid ItemId { get; init; }
}

public sealed record ImportSessionProbeItemRef
{
    public required Guid ItemId { get; init; }
    public required string RelativePath { get; init; }
}

public sealed record EnrichImportSessionItemCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    public required Guid SessionId { get; init; }
    public required Guid ItemId { get; init; }
    public required string SourceUrl { get; init; }
    public required string RelativePath { get; init; }
    public string? Provider { get; init; }
    public string? RequiredWorkerTag { get; init; }
    public ImportSessionYtDlpOptions Options { get; init; } = new();
}

public sealed record ImportSessionItemEnriched : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid SessionId { get; init; }
    public required Guid ItemId { get; init; }
    public required string EnrichedMetadataJson { get; init; }
    public string? Title { get; init; }
    public string? Provider { get; init; }
    public string? SourceMediaId { get; init; }
    public string? SourceUrl { get; init; }
    public string? InfoJsonRelativePath { get; init; }
}

public sealed record ImportSessionItemEnrichFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid SessionId { get; init; }
    public required Guid ItemId { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record ImportSessionItemsProbed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid SessionId { get; init; }
    public IReadOnlyList<ImportSessionProbeResult> Results { get; init; } = [];
    public IReadOnlyList<ImportSessionProbeFailure> Failures { get; init; } = [];
}

public sealed record ImportSessionItemsProbeFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid SessionId { get; init; }
    public IReadOnlyList<ImportSessionProbeFailure> Failures { get; init; } = [];
}

public sealed record ImportSessionProbeResult
{
    public required Guid ItemId { get; init; }
    public required string ProbeMetadataJson { get; init; }
    public double? DurationSeconds { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
}

public sealed record ImportSessionProbeFailure
{
    public required Guid ItemId { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record ImportSessionScanIngestRequest
{
    public required Guid SessionId { get; init; }
    public required string ObjectBucket { get; init; }
    public required string ObjectKey { get; init; }
    public int ItemCount { get; init; }
}

public sealed record ImportSessionScanIngestResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionScanFailedRequest
{
    public required Guid SessionId { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record ImportSessionScanFailedResponse : ImportSessionOperationResponse
{
    public ImportSessionDto? Session { get; init; }
}

public sealed record ImportSessionScannedItem
{
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public Instant? FileMtime { get; init; }
    public string? SidecarsJson { get; init; }
    public string? Provider { get; init; }
    public string? SourceMediaId { get; init; }
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public string? ScanMetadataJson { get; init; }
    public ImportSessionItemMetadataState MetadataState { get; init; }
    public ImportSessionItemMetadataSource MetadataSource { get; init; }
}

public sealed record ImportSessionStateChanged
{
    public required Guid SessionId { get; init; }
    public ImportSessionStatus Status { get; init; }
    public required Instant OccurredAt { get; init; }
}
