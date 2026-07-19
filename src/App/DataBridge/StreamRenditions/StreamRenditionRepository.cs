using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.StreamRenditions;

public sealed class StreamRenditionRepository(DataBridgeDbContext db, IClock clock) : IStreamRenditionRepository
{
    public async Task<StreamRenditionDto?> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default)
    {
        var source = await ResolveSourceAsync(mediaGuid, storageKey, sourceVersion, cancellationToken);
        if (source is null)
            return null;

        return await db.StreamRenditions
            .AsNoTracking()
            .Where(x => x.MediaGuid == mediaGuid &&
                        x.SourceVersionNum == source.VersionNum &&
                        x.StorageKey == source.StorageKey)
            .Select(x => ToDto(x))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<StreamRenditionDto?> CreateIfMissingAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default)
    {
        var source = await ResolveSourceAsync(mediaGuid, storageKey, sourceVersion, cancellationToken);
        if (source is null)
            return null;

        var existing = await db.StreamRenditions
            .FirstOrDefaultAsync(x =>
                x.MediaGuid == mediaGuid &&
                x.SourceVersionNum == source.VersionNum &&
                x.StorageKey == source.StorageKey,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == StreamRenditionStatus.Failed)
            {
                existing.Status = StreamRenditionStatus.Pending;
                existing.ErrorMessage = null;
                existing.UpdatedAt = clock.GetCurrentInstant();
                await db.SaveChangesAsync(cancellationToken);
            }

            return ToDto(existing);
        }

        var rendition = new StreamRenditionEntity
        {
            RenditionId = Guid.NewGuid(),
            MediaGuid = mediaGuid,
            SourceVersionNum = source.VersionNum,
            Status = StreamRenditionStatus.Pending,
            StorageKey = source.StorageKey,
            StoragePath = BuildStoragePath(mediaGuid, source.VersionNum),
            UpdatedAt = clock.GetCurrentInstant()
        };

        db.StreamRenditions.Add(rendition);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rendition);
    }

    public async Task<StreamRenditionWorkItem?> ClaimAsync(Guid renditionId, CancellationToken cancellationToken = default)
    {
        var rendition = await db.StreamRenditions.FirstOrDefaultAsync(x => x.RenditionId == renditionId, cancellationToken);
        if (rendition is null)
            return null;

        if (rendition.Status == StreamRenditionStatus.Ready)
            return null;

        var source = await db.MediaContentIdVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.MediaGuid == rendition.MediaGuid && x.VersionNum == rendition.SourceVersionNum, cancellationToken);
        if (source is null)
            return null;

        rendition.Status = StreamRenditionStatus.Processing;
        rendition.ErrorMessage = null;
        rendition.StoragePath ??= BuildStoragePath(rendition.MediaGuid, rendition.SourceVersionNum);
        rendition.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);

        return new StreamRenditionWorkItem
        {
            RenditionId = rendition.RenditionId,
            MediaGuid = rendition.MediaGuid,
            SourceVersion = rendition.SourceVersionNum,
            SourceStorageKey = source.StorageKey,
            SourceStoragePath = source.StoragePath,
            OutputStorageKey = rendition.StorageKey,
            OutputStoragePath = rendition.StoragePath
        };
    }

    public async Task<bool> CompleteAsync(
        Guid renditionId,
        string storagePath,
        long sizeBytes,
        int? durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var rendition = await db.StreamRenditions.FirstOrDefaultAsync(x => x.RenditionId == renditionId, cancellationToken);
        if (rendition is null)
            return false;

        rendition.Status = StreamRenditionStatus.Ready;
        rendition.StoragePath = storagePath;
        rendition.SizeBytes = sizeBytes;
        rendition.DurationSeconds = durationSeconds;
        rendition.ErrorMessage = null;
        rendition.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FailAsync(Guid renditionId, string errorMessage, CancellationToken cancellationToken = default)
    {
        var rendition = await db.StreamRenditions.FirstOrDefaultAsync(x => x.RenditionId == renditionId, cancellationToken);
        if (rendition is null)
            return false;

        rendition.Status = StreamRenditionStatus.Failed;
        rendition.ErrorMessage = errorMessage.Length > 4096 ? errorMessage[..4096] : errorMessage;
        rendition.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<MediaContentIdVersionEntity?> ResolveSourceAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken)
    {
        var query = db.MediaContentIdVersions
            .AsNoTracking()
            .Where(x => x.MediaGuid == mediaGuid);

        if (!string.IsNullOrWhiteSpace(storageKey))
            query = query.Where(x => x.StorageKey == storageKey);

        if (sourceVersion is not null)
            query = query.Where(x => x.VersionNum == sourceVersion.Value);

        return await query
            .OrderByDescending(x => x.VersionNum)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Beside the archived original: data/archives/<guid>/<version>/stream/hls
    private static string BuildStoragePath(Guid mediaGuid, int sourceVersion)
        => $"archives/{mediaGuid:N}/v{sourceVersion}/stream/hls";

    private static StreamRenditionDto ToDto(StreamRenditionEntity entity)
        => new()
        {
            RenditionId = entity.RenditionId,
            MediaGuid = entity.MediaGuid,
            SourceVersion = entity.SourceVersionNum,
            Status = entity.Status,
            StorageKey = entity.StorageKey,
            StoragePath = entity.StoragePath,
            SizeBytes = entity.SizeBytes,
            DurationSeconds = entity.DurationSeconds,
            ErrorMessage = entity.ErrorMessage,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
}
