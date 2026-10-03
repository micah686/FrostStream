using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Metadata;

namespace DataBridge.Data;

public sealed class MetadataRepository(DataBridgeDbContext db) : IMetadataRepository
{
    public async Task<long> UpsertAccountAssetsAsync(
        string platform,
        string accountHandle,
        string accountName,
        string? accountUrl,
        string? avatarStoragePath,
        string? bannerStoragePath,
        string storageKey,
        CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            // Don't overwrite account_name on conflict: a media-download write may already have
            // recorded a better name than a channel refresh can derive.
            await using var cmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.UpsertAccountAssetsAsync.1"), conn, db.Database.CurrentTransaction?.GetDbTransaction());

            cmd.Parameters.AddWithValue("@platform", platform);
            cmd.Parameters.AddWithValue("@account_name", accountName);
            cmd.Parameters.AddWithValue("@account_handle", accountHandle);
            cmd.Parameters.AddWithValue("@account_url", (object?)accountUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@avatar_path", (object?)avatarStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@banner_path", (object?)bannerStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@storage_key", (object?)storageKey ?? DBNull.Value);
            // The DO UPDATE branch always touches the row, so RETURNING yields the id on both paths.
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        finally
        {
            if (opened)
                await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> UpdateAccountAssetsByIdAsync(
        long accountId,
        string? avatarStoragePath,
        string? bannerStoragePath,
        string? storageKey,
        CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            await using var cmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.UpdateAccountAssetsByIdAsync.1"), conn, db.Database.CurrentTransaction?.GetDbTransaction());

            cmd.Parameters.AddWithValue("@account_id", accountId);
            cmd.Parameters.AddWithValue("@avatar_path", (object?)avatarStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@banner_path", (object?)bannerStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@storage_key", (object?)storageKey ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            if (opened)
                await db.Database.CloseConnectionAsync();
        }
    }

    public Task WriteMetadataAsync(Guid mediaGuid, CapturedMediaMetadata metadata, string storageKey, CancellationToken ct = default)
        => db.MutateAsync("MetadataRepository.WriteMetadataAsync", () => WriteMetadataAsyncCore(mediaGuid, metadata, storageKey, ct), ct);

    private async Task WriteMetadataAsyncCore(Guid mediaGuid, CapturedMediaMetadata metadata, string storageKey, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction!.GetDbTransaction();

        var accountId = await UpsertAccountAsync(conn, transaction, metadata.Account, ct);
        var mediaMetadataId = await InsertMediaMetadataAsync(conn, transaction, mediaGuid, accountId, metadata.Media, storageKey, ct);
        await InsertTaxonomyAsync(conn, transaction, mediaMetadataId, metadata, ct);
        await InsertTechnicalAsync(conn, transaction, mediaGuid, metadata.Technical, ct);
        await InsertCaptionsAsync(conn, transaction, mediaGuid, metadata.Captions, storageKey, ct);
        await InsertCommentsAsync(conn, transaction, mediaGuid, accountId, metadata.Comments, ct);
        await InsertSeriesAsync(conn, transaction, mediaGuid, metadata.Series, ct);
        await InsertMusicAsync(conn, transaction, mediaGuid, metadata.Music, ct);
    }

    // ── accounts ────────────────────────────────────────────────────────────

    private static async Task<long> UpsertAccountAsync(
        DbConnection conn,
        DbTransaction tx,
        CapturedAccountMetadata account,
        CancellationToken ct)
    {
        var aliasedAccountId = await FindAccountIdByExternalIdsAsync(conn, tx, account, ct);
        if (aliasedAccountId is { } existingAccountId)
        {
            await using var update = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.UpsertAccountAsync.1"), conn, tx);

            update.Parameters.AddWithValue("@account_id", existingAccountId);
            update.Parameters.AddWithValue("@account_name", account.AccountName);
            update.Parameters.AddWithValue("@account_url", (object?)account.AccountUrl ?? DBNull.Value);
            update.Parameters.AddWithValue("@follower_count", (object?)account.FollowerCount ?? DBNull.Value);
            update.Parameters.AddWithValue("@description", (object?)account.Description ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(ct);
            await AddAccountExternalIdsAsync(conn, tx, existingAccountId, account, ct);
            return existingAccountId;
        }

        await using var cmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.UpsertAccountAsync.2"), conn, tx);

        cmd.Parameters.AddWithValue("@platform", account.Platform);
        cmd.Parameters.AddWithValue("@account_name", account.AccountName);
        cmd.Parameters.AddWithValue("@account_handle", account.AccountHandle);
        cmd.Parameters.AddWithValue("@account_url", (object?)account.AccountUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@follower_count", (object?)account.FollowerCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description", (object?)account.Description ?? DBNull.Value);

        var accountId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        await AddAccountExternalIdsAsync(conn, tx, accountId, account, ct);
        return accountId;
    }

    private static async Task<long?> FindAccountIdByExternalIdsAsync(
        DbConnection conn,
        DbTransaction tx,
        CapturedAccountMetadata account,
        CancellationToken ct)
    {
        foreach (var externalId in NormalizeExternalIds(account.ExternalIds))
        {
            await using var cmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.FindAccountIdByExternalIdsAsync.1"), conn, tx);

            cmd.Parameters.AddWithValue("@platform", account.Platform);
            cmd.Parameters.AddWithValue("@external_id", externalId.Value);

            if (await cmd.ExecuteScalarAsync(ct) is { } value)
                return Convert.ToInt64(value);
        }

        return null;
    }

    private static async Task AddAccountExternalIdsAsync(
        DbConnection conn,
        DbTransaction tx,
        long accountId,
        CapturedAccountMetadata account,
        CancellationToken ct)
    {
        foreach (var externalId in NormalizeExternalIds(account.ExternalIds))
        {
            await using var cmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.AddAccountExternalIdsAsync.1"), conn, tx);

            cmd.Parameters.AddWithValue("@account_id", accountId);
            cmd.Parameters.AddWithValue("@platform", account.Platform);
            cmd.Parameters.AddWithValue("@id_kind", externalId.Kind);
            cmd.Parameters.AddWithValue("@external_id", externalId.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static IReadOnlyList<CapturedAccountExternalId> NormalizeExternalIds(IReadOnlyList<CapturedAccountExternalId> externalIds)
    {
        HashSet<string>? seen = null;
        List<CapturedAccountExternalId>? result = null;

        foreach (var externalId in externalIds)
        {
            if (string.IsNullOrWhiteSpace(externalId.Kind) || string.IsNullOrWhiteSpace(externalId.Value))
                continue;

            var kind = externalId.Kind.Trim();
            var value = externalId.Value.Trim();
            var key = $"{kind}\u001F{value}";
            seen ??= [];
            if (!seen.Add(key))
                continue;

            result ??= [];
            result.Add(new CapturedAccountExternalId
            {
                Kind = kind,
                Value = value
            });
        }

        return result ?? [];
    }

    // ── media_metadata ───────────────────────────────────────────────────────

    private static async Task<long> InsertMediaMetadataAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid mediaGuid,
        long accountId,
        CapturedMediaMetadataCore core,
        string storageKey,
        CancellationToken ct)
    {
        await using var del = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.InsertMediaMetadataAsync.1"), conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        await using var ins = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertMediaMetadataAsync.2"), conn, tx);

        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@account_id", accountId);
        ins.Parameters.AddWithValue("@external_media_id", (object?)core.ExternalMediaId ?? DBNull.Value);
        ins.Parameters.AddWithValue("@scrape_date", core.MetadataScrapeDate.ToDateTimeUtc());
        ins.Parameters.AddWithValue("@thumbnail", (object?)core.ThumbnailStoragePath ?? DBNull.Value);
        ins.Parameters.AddWithValue("@storage_key", (object?)(core.ThumbnailStoragePath is null ? null : storageKey) ?? DBNull.Value);
        ins.Parameters.AddWithValue("@age_limit", (object?)core.AgeLimit ?? DBNull.Value);
        ins.Parameters.AddWithValue("@avg_rating", (object?)core.AverageRating ?? DBNull.Value);
        ins.Parameters.AddWithValue("@like_count", (object?)core.LikeCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@dislike_count", (object?)core.DislikeCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@duration", (object?)core.DurationSeconds ?? DBNull.Value);
        ins.Parameters.AddWithValue("@description", (object?)core.Description ?? DBNull.Value);
        ins.Parameters.AddWithValue("@release_date", core.ReleaseDate.HasValue ? (object)core.ReleaseDate.Value.ToDateTimeUtc() : DBNull.Value);
        ins.Parameters.AddWithValue("@title", (object?)core.Title ?? DBNull.Value);
        ins.Parameters.AddWithValue("@was_live", core.WasLive);
        ins.Parameters.AddWithValue("@webpage_url", (object?)core.WebpageUrl ?? DBNull.Value);
        ins.Parameters.AddWithValue("@view_count", (object?)core.ViewCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@comment_count", (object?)core.CommentCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@availability", core.Availability ?? "");
        ins.Parameters.AddWithValue("@location", (object?)core.Location ?? DBNull.Value);

        return Convert.ToInt64(await ins.ExecuteScalarAsync(ct));
    }

    // ── taxonomy (artists, genres, tags, categories, cast) ──────────────────

    private static async Task InsertTaxonomyAsync(
        DbConnection conn,
        DbTransaction tx,
        long mediaMetadataId,
        CapturedMediaMetadata metadata,
        CancellationToken ct)
    {
        foreach (var artist in metadata.Artists)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.artists", "artist_name", artist, ct);
            await InsertJunctionAsync(conn, tx,
                conn.Sql("MetadataRepository.InsertTaxonomyAsync.1"),
                mediaMetadataId, id, ct);
        }

        foreach (var artist in metadata.AlbumArtists)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.artists", "artist_name", artist, ct);
            await InsertJunctionAsync(conn, tx,
                conn.Sql("MetadataRepository.InsertTaxonomyAsync.2"),
                mediaMetadataId, id, ct);
        }

        foreach (var genre in metadata.Genres)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.genres", "genre_name", genre, ct);
            await InsertJunctionAsync(conn, tx,
                conn.Sql("MetadataRepository.InsertTaxonomyAsync.3"),
                mediaMetadataId, id, ct);
        }

        foreach (var tag in metadata.Tags)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.tags", "tag_name", tag, ct);
            await InsertJunctionAsync(conn, tx,
                conn.Sql("MetadataRepository.InsertTaxonomyAsync.4"),
                mediaMetadataId, id, ct);
        }

        foreach (var category in metadata.Categories)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.categories", "category_name", category, ct);
            await InsertJunctionAsync(conn, tx,
                conn.Sql("MetadataRepository.InsertTaxonomyAsync.5"),
                mediaMetadataId, id, ct);
        }

        foreach (var cast in metadata.Cast)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.cast_members", "cast_name", cast, ct);
            await InsertJunctionAsync(conn, tx,
                conn.Sql("MetadataRepository.InsertTaxonomyAsync.6"),
                mediaMetadataId, id, ct);
        }
    }

    private static async Task<long> UpsertLookupAsync(
        DbConnection conn,
        DbTransaction tx,
        string table,
        string column,
        string value,
        CancellationToken ct)
    {
        await using var cmd = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.UpsertLookupAsync.1", conn.Table(table), column, column, column, column),
            conn, tx);
        cmd.Parameters.AddWithValue("@v", value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertJunctionAsync(
        DbConnection conn,
        DbTransaction tx,
        string sql,
        long mediaMetadataId,
        long refId,
        CancellationToken ct)
    {
        await using var cmd = ApplicationDbCommands.Create(sql, conn, tx);
        cmd.Parameters.AddWithValue("@mm", mediaMetadataId);
        cmd.Parameters.AddWithValue("@ref", refId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── technical (media_base, format, streams, chapters) ───────────────────

    private static async Task InsertTechnicalAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid mediaGuid,
        CapturedMediaTechnicalMetadata technical,
        CancellationToken ct)
    {
        await using var del = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.InsertTechnicalAsync.1"), conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        await using var ins = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.InsertTechnicalAsync.2"),
            conn, tx);
        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@ticks", technical.DurationTicks);
        var mediaBaseId = Convert.ToInt64(await ins.ExecuteScalarAsync(ct));

        var fmt = technical.Format;
        await using var fmtCmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertTechnicalAsync.3"), conn, tx);
        fmtCmd.Parameters.AddWithValue("@base_id", mediaBaseId);
        fmtCmd.Parameters.AddWithValue("@dur", fmt.DurationTicks);
        fmtCmd.Parameters.AddWithValue("@start", fmt.StartTimeTicks);
        fmtCmd.Parameters.AddWithValue("@names", fmt.FormatLongNames);
        fmtCmd.Parameters.AddWithValue("@streams", fmt.StreamCount);
        fmtCmd.Parameters.AddWithValue("@bitrate", fmt.BitRate);
        await fmtCmd.ExecuteNonQueryAsync(ct);

        foreach (var stream in technical.Streams)
        {
            await using var streamCmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertTechnicalAsync.4"), conn, tx);
            streamCmd.Parameters.AddWithValue("@base_id", mediaBaseId);
            streamCmd.Parameters.AddWithValue("@type", stream.StreamType);
            streamCmd.Parameters.AddWithValue("@primary", stream.IsPrimary);
            streamCmd.Parameters.AddWithValue("@codec", stream.CodecName);
            streamCmd.Parameters.AddWithValue("@codec_long", stream.CodecLongName);
            streamCmd.Parameters.AddWithValue("@bitrate", stream.BitRate);
            streamCmd.Parameters.AddWithValue("@depth", (object?)stream.BitDepth ?? DBNull.Value);
            streamCmd.Parameters.AddWithValue("@dur", stream.DurationTicks);
            streamCmd.Parameters.AddWithValue("@lang", (object?)stream.Language ?? DBNull.Value);
            var streamId = Convert.ToInt64(await streamCmd.ExecuteScalarAsync(ct));

            if (stream.Video is { } video)
            {
                await using var vidCmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertTechnicalAsync.5"), conn, tx);
                vidCmd.Parameters.AddWithValue("@id", streamId);
                vidCmd.Parameters.AddWithValue("@fps", video.AvgFrameRate);
                vidCmd.Parameters.AddWithValue("@w", video.Width);
                vidCmd.Parameters.AddWithValue("@h", video.Height);
                vidCmd.Parameters.AddWithValue("@hdr", video.HdrType);
                await vidCmd.ExecuteNonQueryAsync(ct);
            }

            if (stream.Audio is { } audio)
            {
                await using var audCmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertTechnicalAsync.6"), conn, tx);
                audCmd.Parameters.AddWithValue("@id", streamId);
                audCmd.Parameters.AddWithValue("@channels", audio.Channels);
                audCmd.Parameters.AddWithValue("@rate", audio.SampleRateHz);
                await audCmd.ExecuteNonQueryAsync(ct);
            }
        }

        foreach (var chapter in technical.Chapters)
        {
            await using var chapCmd = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertTechnicalAsync.7"), conn, tx);
            chapCmd.Parameters.AddWithValue("@base_id", mediaBaseId);
            chapCmd.Parameters.AddWithValue("@title", chapter.Title);
            chapCmd.Parameters.AddWithValue("@start", chapter.StartTicks);
            chapCmd.Parameters.AddWithValue("@end", chapter.EndTicks);
            await chapCmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ── captions ─────────────────────────────────────────────────────────────

    private static async Task InsertCaptionsAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid mediaGuid,
        IReadOnlyList<CapturedCaptionMetadata> captions,
        string storageKey,
        CancellationToken ct)
    {
        await using var del = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.InsertCaptionsAsync.1"), conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        foreach (var caption in captions)
        {
            await using var ins = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertCaptionsAsync.2"), conn, tx);
            ins.Parameters.AddWithValue("@media_guid", mediaGuid);
            ins.Parameters.AddWithValue("@path", caption.StoragePath);
            ins.Parameters.AddWithValue("@storage_key", storageKey);
            ins.Parameters.AddWithValue("@type", caption.CaptionType);
            ins.Parameters.AddWithValue("@lang", caption.LanguageCode);
            ins.Parameters.AddWithValue("@name", (object?)caption.Name ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    // ── comments ─────────────────────────────────────────────────────────────

    private static async Task InsertCommentsAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid mediaGuid,
        long uploaderAccountId,
        IReadOnlyList<CapturedCommentMetadata> comments,
        CancellationToken ct)
    {
        await using var del = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.InsertCommentsAsync.1"), conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        foreach (var comment in comments)
        {
            var commentAccountId = comment.IsUploader
                ? uploaderAccountId
                : await UpsertAccountAsync(conn, tx, comment.Account, ct);

            if (comment.IsUploader)
                await AddAccountExternalIdsAsync(conn, tx, uploaderAccountId, comment.Account, ct);

            await using var ins = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertCommentsAsync.2"), conn, tx);
            ins.Parameters.AddWithValue("@media_guid", mediaGuid);
            ins.Parameters.AddWithValue("@comment_id", comment.CommentId);
            ins.Parameters.AddWithValue("@parent_id", (object?)comment.ParentCommentId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@text", comment.Text);
            ins.Parameters.AddWithValue("@account_id", commentAccountId);
            ins.Parameters.AddWithValue("@timestamp", comment.CommentTimestamp.ToDateTimeUtc());
            ins.Parameters.AddWithValue("@like_count", (object?)comment.LikeCount ?? DBNull.Value);
            ins.Parameters.AddWithValue("@dislike_count", (object?)comment.DislikeCount ?? DBNull.Value);
            ins.Parameters.AddWithValue("@is_favorited", comment.IsFavorited);
            ins.Parameters.AddWithValue("@is_uploader", comment.IsUploader);
            ins.Parameters.AddWithValue("@is_pinned", comment.IsPinned);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    // ── series ────────────────────────────────────────────────────────────────

    private static async Task InsertSeriesAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid mediaGuid,
        CapturedSeriesMetadata? series,
        CancellationToken ct)
    {
        await using var del = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.InsertSeriesAsync.1"), conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        if (series is null)
            return;

        await using var ins = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertSeriesAsync.2"), conn, tx);
        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@series_name", series.SeriesName);
        ins.Parameters.AddWithValue("@season_count", (object?)series.SeasonCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@season_num", series.SeasonNumber);
        ins.Parameters.AddWithValue("@season_name", (object?)series.SeasonName ?? DBNull.Value);
        ins.Parameters.AddWithValue("@episode_num", series.EpisodeNumber);
        ins.Parameters.AddWithValue("@episode_name", series.EpisodeName);
        await ins.ExecuteNonQueryAsync(ct);
    }

    // ── music ─────────────────────────────────────────────────────────────────

    private static async Task InsertMusicAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid mediaGuid,
        CapturedMusicMetadata? music,
        CancellationToken ct)
    {
        await using var del = ApplicationDbCommands.Create(
            conn.Sql("MetadataRepository.InsertMusicAsync.1"), conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        if (music is null)
            return;

        await using var ins = ApplicationDbCommands.Create(conn.Sql("MetadataRepository.InsertMusicAsync.2"), conn, tx);
        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@album_title", music.AlbumTitle);
        ins.Parameters.AddWithValue("@album_type", (object?)music.AlbumType ?? DBNull.Value);
        ins.Parameters.AddWithValue("@disc_num", (object?)music.DiscNumber ?? DBNull.Value);
        ins.Parameters.AddWithValue("@release_year", (object?)music.ReleaseYear ?? DBNull.Value);
        ins.Parameters.AddWithValue("@track_title", music.TrackTitle);
        ins.Parameters.AddWithValue("@track_num", music.TrackNumber);
        ins.Parameters.AddWithValue("@composer", (object?)music.Composer ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync(ct);
    }
}
