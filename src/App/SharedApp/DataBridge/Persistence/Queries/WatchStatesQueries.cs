namespace DataBridge.Persistence.Queries;

internal static class WatchStatesQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["WatchStateConsumerService.HandleUpsertAsync.1"] = new(
            """
            INSERT INTO media.watch_states
                (owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, created_at, updated_at)
            VALUES
                (@owner_subject, @media_guid, @position_seconds, @duration_seconds, @completed, @watched_at, @now, @now, @now)
            ON CONFLICT (owner_subject, media_guid)
            DO UPDATE SET
                position_seconds = EXCLUDED.position_seconds,
                duration_seconds = EXCLUDED.duration_seconds,
                completed = EXCLUDED.completed,
                watched_at = CASE
                    WHEN EXCLUDED.completed AND media.watch_states.completed THEN media.watch_states.watched_at
                    WHEN EXCLUDED.completed THEN EXCLUDED.watched_at
                    ELSE NULL
                END,
                last_played_at = EXCLUDED.last_played_at,
                updated_at = EXCLUDED.updated_at
            RETURNING owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, updated_at;
            """,
            """
            INSERT INTO media_watch_states
                (owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, created_at, updated_at)
            VALUES
                (@owner_subject, @media_guid, @position_seconds, @duration_seconds, @completed, @watched_at, @now, @now, @now)
            ON CONFLICT (owner_subject, media_guid)
            DO UPDATE SET
                position_seconds = EXCLUDED.position_seconds,
                duration_seconds = EXCLUDED.duration_seconds,
                completed = EXCLUDED.completed,
                watched_at = CASE
                    WHEN EXCLUDED.completed AND media_watch_states.completed THEN media_watch_states.watched_at
                    WHEN EXCLUDED.completed THEN EXCLUDED.watched_at
                    ELSE NULL
                END,
                last_played_at = EXCLUDED.last_played_at,
                updated_at = EXCLUDED.updated_at
            RETURNING owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, updated_at;
            """),
        ["WatchStateConsumerService.HandleGetAsync.1"] = new(
            """
            SELECT owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, updated_at
            FROM media.watch_states
            WHERE owner_subject = @owner_subject AND media_guid = @media_guid;
            """,
            """
            SELECT owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, updated_at
            FROM media_watch_states
            WHERE owner_subject = @owner_subject AND media_guid = @media_guid;
            """),
        ["WatchStateConsumerService.HandleListInProgressAsync.1"] = new(
            """
            SELECT ws.owner_subject, ws.media_guid, ws.position_seconds, ws.duration_seconds,
                   ws.completed, ws.watched_at, ws.last_played_at, ws.updated_at
            FROM media.watch_states ws
            JOIN media.media m ON m.media_guid = ws.media_guid
            WHERE ws.owner_subject = @owner_subject
              AND NOT ws.completed
              AND ws.position_seconds IS NOT NULL
              AND ws.position_seconds > 0
            ORDER BY ws.last_played_at DESC
            LIMIT @limit;
            """,
            """
            SELECT ws.owner_subject, ws.media_guid, ws.position_seconds, ws.duration_seconds,
                   ws.completed, ws.watched_at, ws.last_played_at, ws.updated_at
            FROM media_watch_states ws
            JOIN media_media m ON m.media_guid = ws.media_guid
            WHERE ws.owner_subject = @owner_subject
              AND NOT ws.completed
              AND ws.position_seconds IS NOT NULL
              AND ws.position_seconds > 0
            ORDER BY ws.last_played_at DESC
            LIMIT @limit;
            """),
        ["WatchStateConsumerService.HandleListHistoryAsync.1"] = new(
            """
            SELECT ws.owner_subject, ws.media_guid, ws.position_seconds, ws.duration_seconds,
                   ws.completed, ws.watched_at, ws.last_played_at, ws.updated_at,
                   COUNT(*) OVER() AS total_count,
                   COALESCE(mm.title, '') AS title,
                   mm.thumbnail_storage_path,
                   mm.duration,
                   mm.release_date,
                   mm.view_count,
                   mm.availability::text AS availability,
                   mm.was_live,
                   a.id AS account_id,
                   a.platform,
                   a.account_name,
                   a.account_handle,
                   a.avatar_storage_path
            FROM media.watch_states ws
            JOIN metadata.media_metadata mm ON mm.media_guid = ws.media_guid
            JOIN metadata.accounts a ON a.id = mm.account_id
            WHERE ws.owner_subject = @owner_subject
            ORDER BY ws.last_played_at DESC
            LIMIT @limit OFFSET @offset;
            """,
            """
            SELECT ws.owner_subject, ws.media_guid, ws.position_seconds, ws.duration_seconds,
                   ws.completed, ws.watched_at, ws.last_played_at, ws.updated_at,
                   COUNT(*) OVER() AS total_count,
                   COALESCE(mm.title, '') AS title,
                   mm.thumbnail_storage_path,
                   mm.duration,
                   mm.release_date,
                   mm.view_count,
                   mm.availability AS availability,
                   mm.was_live,
                   a.id AS account_id,
                   a.platform,
                   a.account_name,
                   a.account_handle,
                   a.avatar_storage_path
            FROM media_watch_states ws
            JOIN metadata_media_metadata mm ON mm.media_guid = ws.media_guid
            JOIN metadata_accounts a ON a.id = mm.account_id
            WHERE ws.owner_subject = @owner_subject
            ORDER BY ws.last_played_at DESC
            LIMIT @limit OFFSET @offset;
            """),
        ["WatchStateConsumerService.HandleGetLikeAsync.1"] = new(
            """
            SELECT owner_subject, media_guid, liked_at, updated_at
            FROM media.user_media_likes
            WHERE owner_subject = @owner_subject AND media_guid = @media_guid;
            """,
            """
            SELECT owner_subject, media_guid, liked_at, updated_at
            FROM media_user_media_likes
            WHERE owner_subject = @owner_subject AND media_guid = @media_guid;
            """),
        ["WatchStateConsumerService.HandleLikeAsync.1"] = new(
            """
            INSERT INTO media.user_media_likes
                (owner_subject, media_guid, liked_at, updated_at)
            VALUES
                (@owner_subject, @media_guid, @now, @now)
            ON CONFLICT (owner_subject, media_guid)
            DO UPDATE SET
                updated_at = EXCLUDED.updated_at
            RETURNING owner_subject, media_guid, liked_at, updated_at;
            """,
            """
            INSERT INTO media_user_media_likes
                (owner_subject, media_guid, liked_at, updated_at)
            VALUES
                (@owner_subject, @media_guid, @now, @now)
            ON CONFLICT (owner_subject, media_guid)
            DO UPDATE SET
                updated_at = EXCLUDED.updated_at
            RETURNING owner_subject, media_guid, liked_at, updated_at;
            """),
        ["WatchStateConsumerService.HandleUnlikeAsync.1"] = new(
            """
            DELETE FROM media.user_media_likes
            WHERE owner_subject = @owner_subject AND media_guid = @media_guid;
            """,
            """
            DELETE FROM media_user_media_likes
            WHERE owner_subject = @owner_subject AND media_guid = @media_guid;
            """),
        ["WatchStateConsumerService.HandleListLikesAsync.1"] = new(
            """
            SELECT uml.owner_subject, uml.media_guid, uml.liked_at, uml.updated_at,
                   COUNT(*) OVER() AS total_count,
                   COALESCE(mm.title, '') AS title,
                   mm.thumbnail_storage_path,
                   mm.duration,
                   mm.release_date,
                   mm.view_count,
                   mm.availability::text AS availability,
                   mm.was_live,
                   a.id AS account_id,
                   a.platform,
                   a.account_name,
                   a.account_handle,
                   a.avatar_storage_path
            FROM media.user_media_likes uml
            JOIN metadata.media_metadata mm ON mm.media_guid = uml.media_guid
            JOIN metadata.accounts a ON a.id = mm.account_id
            WHERE uml.owner_subject = @owner_subject
            ORDER BY uml.liked_at DESC
            LIMIT @limit OFFSET @offset;
            """,
            """
            SELECT uml.owner_subject, uml.media_guid, uml.liked_at, uml.updated_at,
                   COUNT(*) OVER() AS total_count,
                   COALESCE(mm.title, '') AS title,
                   mm.thumbnail_storage_path,
                   mm.duration,
                   mm.release_date,
                   mm.view_count,
                   mm.availability AS availability,
                   mm.was_live,
                   a.id AS account_id,
                   a.platform,
                   a.account_name,
                   a.account_handle,
                   a.avatar_storage_path
            FROM media_user_media_likes uml
            JOIN metadata_media_metadata mm ON mm.media_guid = uml.media_guid
            JOIN metadata_accounts a ON a.id = mm.account_id
            WHERE uml.owner_subject = @owner_subject
            ORDER BY uml.liked_at DESC
            LIMIT @limit OFFSET @offset;
            """),
        ["WatchStateConsumerService.MediaExistsAsync.1"] = new(
            """
            SELECT EXISTS (SELECT 1 FROM media.media WHERE media_guid = @media_guid);
            """,
            """
            SELECT EXISTS (SELECT 1 FROM media_media WHERE media_guid = @media_guid);
            """),
    };
}
