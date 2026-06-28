using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.AudioRenditions;

public sealed class AudioRenditionRepository(DataBridgeDbContext db, IClock clock) : IAudioRenditionRepository
{
    public async Task<AudioRenditionDto?> ResolveAsync(
        Guid mediaGuid,
        AudioRenditionFormat format,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default)
    {
        var source = await ResolveSourceAsync(mediaGuid, storageKey, sourceVersion, cancellationToken);
        if (source is null)
            return null;

        return await db.AudioRenditions
            .AsNoTracking()
            .Where(x => x.MediaGuid == mediaGuid &&
                        x.SourceVersionNum == source.VersionNum &&
                        x.Format == format &&
                        x.StorageKey == source.StorageKey)
            .Select(x => ToDto(x))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AudioRenditionDto?> CreateIfMissingAsync(
        Guid mediaGuid,
        AudioRenditionFormat format,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default)
    {
        var source = await ResolveSourceAsync(mediaGuid, storageKey, sourceVersion, cancellationToken);
        if (source is null)
            return null;

        var existing = await db.AudioRenditions
            .FirstOrDefaultAsync(x =>
                x.MediaGuid == mediaGuid &&
                x.SourceVersionNum == source.VersionNum &&
                x.Format == format &&
                x.StorageKey == source.StorageKey,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == AudioRenditionStatus.Failed)
            {
                existing.Status = AudioRenditionStatus.Pending;
                existing.ErrorMessage = null;
                existing.UpdatedAt = clock.GetCurrentInstant();
                await db.SaveChangesAsync(cancellationToken);
            }

            return ToDto(existing);
        }

        var rendition = new AudioRenditionEntity
        {
            RenditionId = Guid.NewGuid(),
            MediaGuid = mediaGuid,
            SourceVersionNum = source.VersionNum,
            Format = format,
            Status = AudioRenditionStatus.Pending,
            StorageKey = source.StorageKey,
            StoragePath = BuildStoragePath(mediaGuid, source.VersionNum, format),
            UpdatedAt = clock.GetCurrentInstant()
        };

        db.AudioRenditions.Add(rendition);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rendition);
    }

    public async Task<AudioRenditionWorkItem?> ClaimAsync(Guid renditionId, CancellationToken cancellationToken = default)
    {
        var rendition = await db.AudioRenditions.FirstOrDefaultAsync(x => x.RenditionId == renditionId, cancellationToken);
        if (rendition is null)
            return null;

        if (rendition.Status == AudioRenditionStatus.Ready)
            return null;

        var source = await db.MediaContentIdVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.MediaGuid == rendition.MediaGuid && x.VersionNum == rendition.SourceVersionNum, cancellationToken);
        if (source is null)
            return null;

        rendition.Status = AudioRenditionStatus.Processing;
        rendition.ErrorMessage = null;
        rendition.StoragePath ??= BuildStoragePath(rendition.MediaGuid, rendition.SourceVersionNum, rendition.Format);
        rendition.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);

        return new AudioRenditionWorkItem
        {
            RenditionId = rendition.RenditionId,
            MediaGuid = rendition.MediaGuid,
            SourceVersion = rendition.SourceVersionNum,
            Format = rendition.Format,
            SourceStorageKey = source.StorageKey,
            SourceStoragePath = source.StoragePath,
            OutputStorageKey = rendition.StorageKey,
            OutputStoragePath = rendition.StoragePath
        };
    }

    public async Task<bool> CompleteAsync(
        Guid renditionId,
        string storagePath,
        string contentHashXxh128,
        long sizeBytes,
        int? durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var rendition = await db.AudioRenditions.FirstOrDefaultAsync(x => x.RenditionId == renditionId, cancellationToken);
        if (rendition is null)
            return false;

        rendition.Status = AudioRenditionStatus.Ready;
        rendition.StoragePath = storagePath;
        rendition.ContentHashXxh128 = contentHashXxh128;
        rendition.SizeBytes = sizeBytes;
        rendition.DurationSeconds = durationSeconds;
        rendition.ErrorMessage = null;
        rendition.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FailAsync(Guid renditionId, string errorMessage, CancellationToken cancellationToken = default)
    {
        var rendition = await db.AudioRenditions.FirstOrDefaultAsync(x => x.RenditionId == renditionId, cancellationToken);
        if (rendition is null)
            return false;

        rendition.Status = AudioRenditionStatus.Failed;
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

    private static string BuildStoragePath(Guid mediaGuid, int sourceVersion, AudioRenditionFormat format)
        => $"media/{mediaGuid:N}/audio/v{sourceVersion}/{format.ToString().ToLowerInvariant()}/audio.{Extension(format)}";

    private static string Extension(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Aac => "m4a",
            AudioRenditionFormat.Opus => "opus",
            AudioRenditionFormat.Mp3 => "mp3",
            _ => "bin"
        };

    private static AudioRenditionDto ToDto(AudioRenditionEntity entity)
        => new()
        {
            RenditionId = entity.RenditionId,
            MediaGuid = entity.MediaGuid,
            SourceVersion = entity.SourceVersionNum,
            Format = entity.Format,
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
