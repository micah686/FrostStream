using Shared.Messaging;

namespace DataBridge.Data;

public interface ILocalImportRepository
{
    Task CreateBatchIfMissingAsync(LocalMediaImportRequested request, CancellationToken ct = default);

    Task MarkBatchPreparingAsync(Guid batchId, int totalItems, CancellationToken ct = default);

    Task MarkBatchFailedAsync(Guid batchId, string errorMessage, CancellationToken ct = default);

    Task CompleteBatchAsync(Guid batchId, CancellationToken ct = default);

    Task<IReadOnlyList<LocalImportCreatedItem>> CreateItemsIfMissingAsync(
        Guid batchId,
        IReadOnlyList<LocalImportItemCreate> items,
        CancellationToken ct = default);

    Task MarkItemPreparingAsync(Guid itemId, CancellationToken ct = default);

    Task MarkItemPreparedAsync(Guid itemId, LocalImportFilePrepared prepared, CancellationToken ct = default);

    Task MarkItemUploadingAsync(Guid itemId, Guid mediaGuid, string storagePath, CancellationToken ct = default);

    Task MarkItemAlreadyImportedAsync(
        Guid itemId,
        Guid mediaGuid,
        string storagePath,
        LocalImportFilePrepared prepared,
        CancellationToken ct = default);

    Task MarkItemCompletedAsync(
        Guid itemId,
        Guid mediaGuid,
        string storagePath,
        string? storageVersion,
        string? metaStoragePath,
        string? infoJsonStoragePath,
        string? thumbnailStoragePath,
        string? captionStoragePathsJson,
        CancellationToken ct = default);

    Task MarkItemFailedAsync(
        Guid itemId,
        string? errorCode,
        string errorMessage,
        Guid? mediaGuid = null,
        string? storagePath = null,
        CancellationToken ct = default);
}

public sealed record LocalImportItemCreate
{
    public required int ItemIndex { get; init; }

    public required string SourceRoot { get; init; }

    public required string RelativePath { get; init; }

    public required string StorageKey { get; init; }

    public string? Provider { get; init; }

    public string? SourceMediaId { get; init; }

    public NodaTime.Instant? SourceLastModified { get; init; }

    public string? SourceUrl { get; init; }

    public string? Title { get; init; }
}

public sealed record LocalImportCreatedItem(Guid ItemId, int ItemIndex);
