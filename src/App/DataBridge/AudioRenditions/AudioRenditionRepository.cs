using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.AudioRenditions;

public sealed class AudioRenditionRepository(
    DataBridgeDbContext db,
    NpgsqlDataSource dataSource,
    IClock clock) : IAudioRenditionRepository
{
    public async Task<ChannelAudioResolveResult?> ResolveChannelAsync(
        long accountId,
        bool createIfMissing,
        bool retryFailedAndPending,
        CancellationToken cancellationToken = default)
    {
        var account = await ReadChannelAccountAsync(accountId, cancellationToken);
        if (account is null)
            return null;

        var sources = await ReadChannelSourcesAsync(accountId, cancellationToken);
        var mediaGuids = sources.Select(x => x.MediaGuid).ToArray();
        var renditions = mediaGuids.Length == 0
            ? []
            : await db.AudioRenditions
                .Where(x => mediaGuids.Contains(x.MediaGuid))
                .ToListAsync(cancellationToken);

        var bySource = renditions.ToDictionary(
            x => (x.MediaGuid, x.SourceVersionNum, x.StorageKey),
            x => x);
        IReadOnlyList<AudioRenditionDto> renditionsToQueue = [];

        if (createIfMissing)
        {
            var queue = new List<AudioRenditionEntity>();
            foreach (var source in sources)
            {
                var key = (source.MediaGuid, source.SourceVersion, source.StorageKey);
                if (!bySource.TryGetValue(key, out var rendition))
                {
                    rendition = new AudioRenditionEntity
                    {
                        RenditionId = Guid.NewGuid(),
                        MediaGuid = source.MediaGuid,
                        SourceVersionNum = source.SourceVersion,
                        Status = AudioRenditionStatus.Pending,
                        StorageKey = source.StorageKey,
                        StoragePath = BuildStoragePath(source.MediaGuid, source.SourceVersion),
                        UpdatedAt = clock.GetCurrentInstant()
                    };
                    db.AudioRenditions.Add(rendition);
                    bySource.Add(key, rendition);
                    queue.Add(rendition);
                }
                else if (retryFailedAndPending && rendition.Status == AudioRenditionStatus.Failed)
                {
                    rendition.Status = AudioRenditionStatus.Pending;
                    rendition.ErrorMessage = null;
                    rendition.UpdatedAt = clock.GetCurrentInstant();
                    queue.Add(rendition);
                }
                else if (retryFailedAndPending && rendition.Status == AudioRenditionStatus.Pending)
                {
                    queue.Add(rendition);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            renditionsToQueue = queue.Select(ToDto).ToArray();
        }

        var items = sources.Select(source =>
        {
            bySource.TryGetValue((source.MediaGuid, source.SourceVersion, source.StorageKey), out var rendition);
            return new ChannelAudioItemDto
            {
                MediaGuid = source.MediaGuid,
                Title = source.Title,
                Description = source.Description,
                ReleaseDate = source.ReleaseDate,
                DurationSeconds = source.DurationSeconds,
                Rendition = rendition is null ? null : ToDto(rendition)
            };
        }).ToArray();

        var channel = new ChannelAudioDto
        {
            AccountId = account.Value.AccountId,
            AccountName = account.Value.AccountName,
            AccountDescription = account.Value.Description,
            AvatarStoragePath = account.Value.AvatarStoragePath,
            TotalCount = items.Length,
            MissingCount = items.Count(x => x.Rendition is null),
            PendingCount = CountStatus(items, AudioRenditionStatus.Pending),
            ProcessingCount = CountStatus(items, AudioRenditionStatus.Processing),
            ReadyCount = CountStatus(items, AudioRenditionStatus.Ready),
            FailedCount = CountStatus(items, AudioRenditionStatus.Failed),
            Items = items
        };
        return new ChannelAudioResolveResult(channel, renditionsToQueue);
    }

    public async Task<AudioRenditionDto?> ResolveAsync(
        Guid mediaGuid,
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
                        x.StorageKey == source.StorageKey)
            .Select(x => ToDto(x))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AudioRenditionDto?> CreateIfMissingAsync(
        Guid mediaGuid,
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
            Status = AudioRenditionStatus.Pending,
            StorageKey = source.StorageKey,
            StoragePath = BuildStoragePath(mediaGuid, source.VersionNum),
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
        rendition.StoragePath ??= BuildStoragePath(rendition.MediaGuid, rendition.SourceVersionNum);
        rendition.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);

        return new AudioRenditionWorkItem
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

    private async Task<(long AccountId, string AccountName, string? Description, string? AvatarStoragePath)?>
        ReadChannelAccountAsync(long accountId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, account_name, account_description, avatar_storage_path
            FROM metadata.accounts
            WHERE id = @account_id
            """);
        command.Parameters.AddWithValue("@account_id", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task<IReadOnlyList<ChannelAudioSource>> ReadChannelSourcesAsync(
        long accountId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                mm.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled'),
                mm.description,
                EXTRACT(EPOCH FROM COALESCE(mm.release_date, media_root.created_at))::bigint,
                CASE WHEN mm.duration IS NULL THEN NULL ELSE ROUND(mm.duration)::integer END,
                source.version_num,
                source.storage_key
            FROM metadata.media_metadata mm
            JOIN media.media media_root ON media_root.media_guid = mm.media_guid
            JOIN LATERAL (
                SELECT version_num, storage_key
                FROM media.media_content_id_versions
                WHERE media_guid = mm.media_guid
                ORDER BY version_num DESC
                LIMIT 1
            ) source ON true
            WHERE mm.account_id = @account_id
            ORDER BY COALESCE(mm.release_date, media_root.created_at) DESC, mm.media_guid
            """);
        command.Parameters.AddWithValue("@account_id", accountId);

        var items = new List<ChannelAudioSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ChannelAudioSource(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Instant.FromUnixTimeSeconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }

        return items;
    }

    private static int CountStatus(IEnumerable<ChannelAudioItemDto> items, AudioRenditionStatus status)
        => items.Count(x => x.Rendition?.Status == status);

    // Beside the archived original: data/archives/<guid>/<version>/stream/audio. Renditions created
    // before this layout keep the storage_path recorded on their row, so old files still serve.
    private static string BuildStoragePath(Guid mediaGuid, int sourceVersion)
        => $"archives/{mediaGuid:N}/v{sourceVersion}/stream/audio/media.opus";

    private static AudioRenditionDto ToDto(AudioRenditionEntity entity)
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

    private sealed record ChannelAudioSource(
        Guid MediaGuid,
        string Title,
        string? Description,
        Instant? ReleaseDate,
        int? DurationSeconds,
        int SourceVersion,
        string StorageKey);
}
