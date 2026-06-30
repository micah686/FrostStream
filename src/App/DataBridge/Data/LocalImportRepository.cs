using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class LocalImportRepository(DataBridgeDbContext db, IClock clock) : ILocalImportRepository
{
    public async Task CreateBatchIfMissingAsync(LocalMediaImportRequested request, CancellationToken ct = default)
    {
        if (await db.LocalImportBatches.AnyAsync(x => x.BatchId == request.BatchId, ct))
            return;

        db.LocalImportBatches.Add(new LocalImportBatchEntity
        {
            BatchId = request.BatchId,
            CorrelationId = request.CorrelationId,
            Status = LocalImportStatus.Queued,
            ManifestObjectBucket = request.ManifestObjectBucket,
            ManifestObjectKey = request.ManifestObjectKey,
            SourceRoot = request.SourceRoot,
            StorageKey = request.StorageKey,
            RequestedBy = request.RequestedBy,
            RequestedByContext = request.RequestedByContext
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task MarkBatchPreparingAsync(Guid batchId, int totalItems, CancellationToken ct = default)
    {
        var batch = await db.LocalImportBatches.FirstAsync(x => x.BatchId == batchId, ct);
        batch.Status = LocalImportStatus.Preparing;
        batch.TotalItems = totalItems;
        batch.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkBatchFailedAsync(Guid batchId, string errorMessage, CancellationToken ct = default)
    {
        var batch = await db.LocalImportBatches.FirstAsync(x => x.BatchId == batchId, ct);
        batch.Status = LocalImportStatus.Failed;
        batch.ErrorMessage = errorMessage;
        batch.UpdatedAt = clock.GetCurrentInstant();
        batch.CompletedAt = batch.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.LocalImportBatches.FirstAsync(x => x.BatchId == batchId, ct);
        var counts = await db.LocalImportItems
            .Where(x => x.BatchId == batchId)
            .GroupBy(x => x.BatchId)
            .Select(g => new
            {
                Completed = g.Count(x => x.Status == LocalImportStatus.Completed),
                AlreadyImported = g.Count(x => x.Status == LocalImportStatus.AlreadyImported),
                Failed = g.Count(x => x.Status == LocalImportStatus.Failed)
            })
            .FirstOrDefaultAsync(ct);

        batch.CompletedItems = counts?.Completed ?? 0;
        batch.AlreadyImportedItems = counts?.AlreadyImported ?? 0;
        batch.FailedItems = counts?.Failed ?? 0;
        batch.Status = batch.FailedItems > 0 ? LocalImportStatus.Failed : LocalImportStatus.Completed;
        batch.UpdatedAt = clock.GetCurrentInstant();
        batch.CompletedAt = batch.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<LocalImportCreatedItem>> CreateItemsIfMissingAsync(
        Guid batchId,
        IReadOnlyList<LocalImportItemCreate> items,
        CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            var exists = await db.LocalImportItems.AnyAsync(
                x => x.BatchId == batchId && x.ItemIndex == item.ItemIndex,
                ct);
            if (exists)
                continue;

            db.LocalImportItems.Add(new LocalImportItemEntity
            {
                ItemId = DeterministicItemId(batchId, item.ItemIndex),
                BatchId = batchId,
                ItemIndex = item.ItemIndex,
                Status = LocalImportStatus.Queued,
                SourceRoot = item.SourceRoot,
                RelativePath = item.RelativePath,
                StorageKey = item.StorageKey,
                Provider = NormalizeOptional(item.Provider),
                SourceMediaId = NormalizeOptional(item.SourceMediaId),
                SourceLastModified = item.SourceLastModified,
                SourceUrl = NormalizeOptional(item.SourceUrl),
                Title = NormalizeOptional(item.Title)
            });
        }

        await db.SaveChangesAsync(ct);

        return await db.LocalImportItems
            .AsNoTracking()
            .Where(x => x.BatchId == batchId)
            .OrderBy(x => x.ItemIndex)
            .Select(x => new LocalImportCreatedItem(x.ItemId, x.ItemIndex))
            .ToListAsync(ct);
    }

    public Task MarkItemPreparingAsync(Guid itemId, CancellationToken ct = default)
        => UpdateItemAsync(itemId, LocalImportStatus.Preparing, ct);

    public async Task MarkItemPreparedAsync(Guid itemId, LocalImportFilePrepared prepared, CancellationToken ct = default)
    {
        var item = await db.LocalImportItems.FirstAsync(x => x.ItemId == itemId, ct);
        item.FileSizeBytes = prepared.FileSizeBytes;
        item.ContentHashXxh128 = NormalizeHash(prepared.ContentHashXxh128);
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkItemUploadingAsync(Guid itemId, Guid mediaGuid, string storagePath, CancellationToken ct = default)
    {
        var item = await db.LocalImportItems.FirstAsync(x => x.ItemId == itemId, ct);
        item.Status = LocalImportStatus.Uploading;
        item.MediaGuid = mediaGuid;
        item.StoragePath = storagePath;
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkItemAlreadyImportedAsync(
        Guid itemId,
        Guid mediaGuid,
        string storagePath,
        LocalImportFilePrepared prepared,
        CancellationToken ct = default)
    {
        var item = await db.LocalImportItems.FirstAsync(x => x.ItemId == itemId, ct);
        item.Status = LocalImportStatus.AlreadyImported;
        item.MediaGuid = mediaGuid;
        item.StoragePath = storagePath;
        item.FileSizeBytes = prepared.FileSizeBytes;
        item.ContentHashXxh128 = NormalizeHash(prepared.ContentHashXxh128);
        item.UpdatedAt = clock.GetCurrentInstant();
        item.CompletedAt = item.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkItemCompletedAsync(
        Guid itemId,
        Guid mediaGuid,
        string storagePath,
        string? storageVersion,
        string? metaStoragePath,
        string? infoJsonStoragePath,
        string? thumbnailStoragePath,
        string? captionStoragePathsJson,
        CancellationToken ct = default)
    {
        var item = await db.LocalImportItems.FirstAsync(x => x.ItemId == itemId, ct);
        item.Status = LocalImportStatus.Completed;
        item.MediaGuid = mediaGuid;
        item.StoragePath = storagePath;
        item.StorageVersion = storageVersion;
        item.MetaStoragePath = metaStoragePath;
        item.InfoJsonStoragePath = infoJsonStoragePath;
        item.ThumbnailStoragePath = thumbnailStoragePath;
        item.CaptionStoragePathsJson = captionStoragePathsJson;
        item.UpdatedAt = clock.GetCurrentInstant();
        item.CompletedAt = item.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkItemFailedAsync(
        Guid itemId,
        string? errorCode,
        string errorMessage,
        Guid? mediaGuid = null,
        string? storagePath = null,
        CancellationToken ct = default)
    {
        var item = await db.LocalImportItems.FirstAsync(x => x.ItemId == itemId, ct);
        item.Status = LocalImportStatus.Failed;
        item.ErrorCode = NormalizeOptional(errorCode);
        item.ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? "Local import item failed." : errorMessage;
        item.MediaGuid = mediaGuid ?? item.MediaGuid;
        item.StoragePath = storagePath ?? item.StoragePath;
        item.UpdatedAt = clock.GetCurrentInstant();
        item.CompletedAt = item.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateItemAsync(Guid itemId, LocalImportStatus status, CancellationToken ct)
    {
        var item = await db.LocalImportItems.FirstAsync(x => x.ItemId == itemId, ct);
        item.Status = status;
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    private static Guid DeterministicItemId(Guid batchId, int itemIndex)
    {
        var seed = $"{batchId:N}:{itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(seed)));
    }

    private static string? NormalizeHash(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}
