using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class PlaylistsRepository(
    DataBridgeDbContext db,
    IClock clock,
    IDownloadJobStateNotifier? stateNotifier = null) : IPlaylistsRepository
{
    private readonly IDownloadJobStateNotifier _stateNotifier = stateNotifier ?? NullDownloadJobStateNotifier.Instance;

    public Task<PlaylistEntity?> GetByIdAsync(Guid playlistId, CancellationToken ct = default)
        => db.Playlists.AsNoTracking().FirstOrDefaultAsync(x => x.PlaylistId == playlistId, ct);

    public Task<PlaylistEntity?> FindBySourceUrlAsync(string sourceUrl, CancellationToken ct = default)
        => db.Playlists.AsNoTracking().FirstOrDefaultAsync(x => x.SourceUrl == sourceUrl, ct);

    public async Task<UpsertResult> CreateOrReuseAsync(PlaylistRequested request, CancellationToken ct = default)
    {
        var existingById = await db.Playlists.FirstOrDefaultAsync(x => x.PlaylistId == request.PlaylistId, ct);
        if (existingById is not null)
        {
            existingById.State = PlaylistState.PendingMetadata;
            existingById.UpdatedAt = clock.GetCurrentInstant();
            existingById.ConfigSetKey = request.ConfigSetKey;
            existingById.EncodeForPlaylist = request.EncodeForPlaylist;
            existingById.AudioFormat = request.AudioFormat;
            existingById.CookieSecretPath = request.CookieSecretPath;
            existingById.YtDlpOptionsJson = request.YtDlpOptions is null ? null : System.Text.Json.JsonSerializer.Serialize(request.YtDlpOptions);
            existingById.Priority = request.Priority;
            existingById.FetchComments = request.FetchComments;
            await db.SaveChangesAsync(ct);
            return new UpsertResult(existingById, WasReused: true);
        }

        var existingByUrl = await db.Playlists.FirstOrDefaultAsync(x => x.SourceUrl == request.SourceUrl, ct);
        if (existingByUrl is not null)
        {
            existingByUrl.State = PlaylistState.PendingMetadata;
            existingByUrl.UpdatedAt = clock.GetCurrentInstant();
            existingByUrl.RequestedBy = request.RequestedBy ?? existingByUrl.RequestedBy;
            existingByUrl.StorageKey = request.StorageKey ?? existingByUrl.StorageKey;
            existingByUrl.ConfigSetKey = request.ConfigSetKey;
            existingByUrl.EncodeForPlaylist = request.EncodeForPlaylist;
            existingByUrl.AudioFormat = request.AudioFormat;
            existingByUrl.CookieSecretPath = request.CookieSecretPath;
            existingByUrl.YtDlpOptionsJson = request.YtDlpOptions is null ? null : System.Text.Json.JsonSerializer.Serialize(request.YtDlpOptions);
            existingByUrl.Priority = request.Priority;
            existingByUrl.FetchComments = request.FetchComments;
            await db.SaveChangesAsync(ct);
            return new UpsertResult(existingByUrl, WasReused: true);
        }

        var entity = new PlaylistEntity
        {
            PlaylistId = request.PlaylistId,
            CorrelationId = request.CorrelationId,
            State = PlaylistState.PendingMetadata,
            SourceUrl = request.SourceUrl,
            RequestedBy = request.RequestedBy,
            StorageKey = request.StorageKey,
            ConfigSetKey = request.ConfigSetKey,
            EncodeForPlaylist = request.EncodeForPlaylist,
            AudioFormat = request.AudioFormat,
            CookieSecretPath = request.CookieSecretPath,
            YtDlpOptionsJson = request.YtDlpOptions is null ? null : System.Text.Json.JsonSerializer.Serialize(request.YtDlpOptions),
            Priority = request.Priority,
            FetchComments = request.FetchComments
        };
        db.Playlists.Add(entity);
        await db.SaveChangesAsync(ct);
        return new UpsertResult(entity, WasReused: false);
    }

    public async Task UpdateStateAsync(Guid playlistId, PlaylistState state, CancellationToken ct = default)
    {
        var playlist = await db.Playlists.FirstOrDefaultAsync(x => x.PlaylistId == playlistId, ct);
        if (playlist is null)
            return;

        playlist.State = state;
        playlist.UpdatedAt = clock.GetCurrentInstant();
        if (state == PlaylistState.MetadataResolved)
            playlist.LastScannedAt = playlist.UpdatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplyMetadataFetchedAsync(Guid playlistId, PlaylistMetadataFetched evt, CancellationToken ct = default)
    {
        var playlist = await db.Playlists.FirstOrDefaultAsync(x => x.PlaylistId == playlistId, ct);
        if (playlist is null)
            return;

        playlist.ProviderPlaylistId = evt.ProviderPlaylistId ?? playlist.ProviderPlaylistId;
        playlist.Title = evt.Title ?? playlist.Title;
        playlist.TotalItems = Math.Max(playlist.TotalItems, evt.TotalItems);
        playlist.State = evt.IsComplete ? PlaylistState.MetadataResolved : PlaylistState.PendingMetadata;
        playlist.UpdatedAt = clock.GetCurrentInstant();
        if (evt.IsComplete)
            playlist.LastScannedAt = playlist.UpdatedAt;
        await db.SaveChangesAsync(ct);

        await UpsertPlaylistMetadataAsync(playlistId, playlist.Title, ct);
    }

    private async Task UpsertPlaylistMetadataAsync(Guid playlistId, string? title, CancellationToken ct)
    {
        var row = await db.PlaylistMetadata.FirstOrDefaultAsync(x => x.PlaylistId == playlistId, ct);
        if (row is null)
        {
            db.PlaylistMetadata.Add(new PlaylistMetadataEntity
            {
                PlaylistId = playlistId,
                Title = title
            });
        }
        else
        {
            row.Title = title ?? row.Title;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task WriteStagingEntriesAsync(Guid playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return;

        var existingIndexes = await db.PlaylistScanEntries
            .Where(x => x.PlaylistId == playlistId)
            .Select(x => x.PlaylistIndex)
            .ToListAsync(ct);
        var existingItemUrls = await db.PlaylistItems
            .Where(x => x.PlaylistId == playlistId)
            .Select(x => x.EntryUrl)
            .ToListAsync(ct);

        var skipIndexes = new HashSet<int>(existingIndexes);
        var skipUrls = new HashSet<string>(existingItemUrls, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (skipIndexes.Contains(entry.PlaylistIndex))
                continue;
            if (skipUrls.Contains(entry.EntryUrl))
                continue;

            db.PlaylistScanEntries.Add(new PlaylistScanEntryEntity
            {
                PlaylistId = playlistId,
                PlaylistIndex = entry.PlaylistIndex,
                EntryUrl = entry.EntryUrl,
                EntryTitle = entry.EntryTitle
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PlaylistScanEntryEntity>> ListStagingEntriesAsync(Guid playlistId, CancellationToken ct = default)
    {
        return await db.PlaylistScanEntries
            .AsNoTracking()
            .Where(x => x.PlaylistId == playlistId)
            .OrderBy(x => x.PlaylistIndex)
            .ToListAsync(ct);
    }

    public async Task FanOutEntryAsync(FanOutEntryRequest request, CancellationToken ct = default)
    {
        var staging = await db.PlaylistScanEntries
            .FirstOrDefaultAsync(x => x.PlaylistId == request.PlaylistId
                && x.PlaylistIndex == request.PlaylistIndex, ct);

        var alreadyFannedOut = await db.PlaylistItems
            .AnyAsync(x => x.PlaylistId == request.PlaylistId
                && x.EntryUrl == request.EntryUrl, ct);

        if (alreadyFannedOut)
        {
            if (staging is not null)
            {
                db.PlaylistScanEntries.Remove(staging);
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        var jobExists = await db.DownloadJobs.AnyAsync(x => x.JobId == request.JobId, ct);
        if (!jobExists)
        {
            db.DownloadJobs.Add(new DownloadJobEntity
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                State = request.InitialState,
                SourceUrl = request.EntryUrl,
                RequestedBy = request.RequestedBy,
                StorageKey = request.StorageKey,
                IgnoredKeyword = request.IgnoredKeyword
            });
        }

        db.PlaylistItems.Add(new PlaylistItemEntity
        {
            PlaylistId = request.PlaylistId,
            JobId = request.JobId,
            PlaylistIndex = request.PlaylistIndex,
            EntryUrl = request.EntryUrl,
            EntryTitle = request.EntryTitle
        });

        if (staging is not null)
            db.PlaylistScanEntries.Remove(staging);

        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> RequeuePlaylistItemAsync(Guid playlistId, Guid jobId, CancellationToken ct = default)
    {
        var item = await db.PlaylistItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.JobId == jobId, ct);
        if (item is null)
            return null;

        var job = await db.DownloadJobs.FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (job is null)
            return null;

        var previousState = job.State;
        job.State = DownloadJobState.Queued;
        job.IgnoredKeyword = null;
        job.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        if (previousState != job.State)
            await _stateNotifier.NotifyAsync(job.JobId, job.State, previousState, job.CorrelationId, ct);
        return item.EntryUrl;
    }

    public async Task<bool> TryLinkMediaGuidAsync(Guid jobId, Guid mediaGuid, CancellationToken ct = default)
    {
        var item = await db.PlaylistItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (item is null)
            return false;

        var alreadyLinked = await db.MediaPlaylistMemberships
            .AnyAsync(x => x.PlaylistId == item.PlaylistId && x.PlaylistIndex == item.PlaylistIndex, ct);
        if (alreadyLinked)
            return true;

        db.MediaPlaylistMemberships.Add(new MediaPlaylistMembershipEntity
        {
            MediaGuid = mediaGuid,
            PlaylistId = item.PlaylistId,
            PlaylistIndex = item.PlaylistIndex
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return true;
        }

        return true;
    }

    public async Task<PlaylistAudioPreference?> GetAudioPreferenceForJobAsync(Guid jobId, CancellationToken ct = default)
        => await (
            from item in db.PlaylistItems.AsNoTracking()
            where item.JobId == jobId
            join playlist in db.Playlists.AsNoTracking() on item.PlaylistId equals playlist.PlaylistId
            select new PlaylistAudioPreference(playlist.EncodeForPlaylist, playlist.AudioFormat, playlist.StorageKey))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PlaylistSummary>> ListAsync(int pageSize, int pageOffset, CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var offset = Math.Max(0, pageOffset);

        var playlists = await db.Playlists
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Skip(offset)
            .Take(size)
            .ToListAsync(ct);

        if (playlists.Count == 0)
            return Array.Empty<PlaylistSummary>();

        var ids = playlists.Select(x => x.PlaylistId).ToArray();

        var counts = await db.PlaylistItems
            .AsNoTracking()
            .Where(item => ids.Contains(item.PlaylistId))
            .Join(db.DownloadJobs.AsNoTracking(),
                item => item.JobId,
                job => job.JobId,
                (item, job) => new { item.PlaylistId, job.State })
            .GroupBy(x => new { x.PlaylistId, x.State })
            .Select(g => new { g.Key.PlaylistId, g.Key.State, Count = g.Count() })
            .ToListAsync(ct);

        var summaries = new List<PlaylistSummary>(playlists.Count);
        foreach (var playlist in playlists)
        {
            var rows = counts.Where(c => c.PlaylistId == playlist.PlaylistId).ToList();
            var (completed, failed, pending) = ClassifyCounts(rows.Select(r => (r.State, r.Count)));
            summaries.Add(new PlaylistSummary(playlist, completed, failed, pending));
        }
        return summaries;
    }

    public async Task<PlaylistDetail?> GetDetailAsync(Guid playlistId, CancellationToken ct = default)
    {
        var playlist = await db.Playlists.AsNoTracking().FirstOrDefaultAsync(x => x.PlaylistId == playlistId, ct);
        if (playlist is null)
            return null;

        var items = await (
            from item in db.PlaylistItems.AsNoTracking()
            where item.PlaylistId == playlistId
            join job in db.DownloadJobs.AsNoTracking() on item.JobId equals job.JobId
            join membership in db.MediaPlaylistMemberships.AsNoTracking()
                on new { item.PlaylistId, item.PlaylistIndex }
                equals new { membership.PlaylistId, membership.PlaylistIndex }
                into membershipJoin
            from m in membershipJoin.DefaultIfEmpty()
            orderby item.PlaylistIndex
            select new PlaylistDetailItem(
                item.PlaylistIndex,
                item.JobId,
                item.EntryUrl,
                item.EntryTitle,
                job.State,
                m == null ? (Guid?)null : m.MediaGuid,
                job.IgnoredKeyword))
            .ToListAsync(ct);

        var (completed, failed, pending) = ClassifyCounts(items.Select(i => (i.JobState, 1)));
        return new PlaylistDetail(playlist, completed, failed, pending, items);
    }

    private static (int Completed, int Failed, int Pending) ClassifyCounts(IEnumerable<(DownloadJobState State, int Count)> rows)
    {
        var completed = 0;
        var failed = 0;
        var pending = 0;
        foreach (var (state, count) in rows)
        {
            switch (state)
            {
                case DownloadJobState.Completed:
                case DownloadJobState.AlreadyDownloaded:
                    completed += count;
                    break;
                case DownloadJobState.FailedPermanent:
                case DownloadJobState.FailedTransient:
                case DownloadJobState.DeadLettered:
                case DownloadJobState.ProviderHalted:
                    failed += count;
                    break;
                default:
                    pending += count;
                    break;
            }
        }
        return (completed, failed, pending);
    }
}
