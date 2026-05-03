using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(6, "Create accounts, media metadata, technical detail, taxonomy, and junction tables")]
public sealed class M006_CreateMediaMetadataSchema : Migration
{
    public override void Up()
    {
        Execute.Sql(
            "CREATE TYPE availability_enum AS ENUM (" +
            "'unknown','private','premium_only','subscriber_only','needs_auth','unlisted','public');");

        Execute.Sql(
            "CREATE TYPE subtitle_type_enum AS ENUM (" +
            "'subtitles','automatic_captions');");

        Create.Table("accounts")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("platform").AsString(50).NotNullable()
            .WithColumn("account_name").AsCustom("text").NotNullable()
            .WithColumn("account_handle").AsCustom("text").NotNullable()
            .WithColumn("account_url").AsCustom("text").Nullable()
            .WithColumn("account_creation_date").AsDateTime().Nullable()
            .WithColumn("account_follower_count").AsInt64().Nullable()
            .WithColumn("is_verified").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("account_description").AsCustom("text").Nullable()
            .WithColumn("avatar_storage_path").AsCustom("text").Nullable()
            .WithColumn("banner_storage_path").AsCustom("text").Nullable();

        Create.Index("ux_accounts_platform_handle")
            .OnTable("accounts")
            .OnColumn("platform").Ascending()
            .OnColumn("account_handle").Ascending()
            .WithOptions().Unique();

        Create.Table("media_metadata")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("account_id").AsInt64().NotNullable()
            .WithColumn("external_media_id").AsCustom("text").Nullable()
            .WithColumn("metadata_scrape_date").AsDateTime().NotNullable()
            .WithColumn("thumbnail_storage_path").AsCustom("text").Nullable()
            .WithColumn("age_limit").AsInt32().Nullable()
            .WithColumn("average_rating").AsDouble().Nullable()
            .WithColumn("like_count").AsInt64().Nullable()
            .WithColumn("dislike_count").AsInt64().Nullable()
            .WithColumn("duration").AsDouble().Nullable()
            .WithColumn("description").AsCustom("text").Nullable()
            .WithColumn("release_date").AsDateTime().Nullable()
            .WithColumn("title").AsCustom("text").Nullable()
            .WithColumn("was_live").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("webpage_url").AsCustom("text").Nullable()
            .WithColumn("view_count").AsInt64().Nullable()
            .WithColumn("comment_count").AsInt64().Nullable()
            .WithColumn("availability").AsCustom("availability_enum").Nullable()
            .WithColumn("location").AsCustom("text").Nullable();

        Create.ForeignKey("fk_media_metadata_account_id")
            .FromTable("media_metadata").ForeignColumn("account_id")
            .ToTable("accounts").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ux_media_metadata_media_guid")
            .OnTable("media_metadata")
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("media_base")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0);

        Create.Index("ix_media_base_media_guid")
            .OnTable("media_base")
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("media_format_details")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("start_time_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("format_long_names").AsCustom("text").NotNullable()
            .WithColumn("stream_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("bit_rate").AsDouble().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("fk_media_format_details_media_base_id")
            .FromTable("media_format_details").ForeignColumn("media_base_id")
            .ToTable("media_base").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_media_format_details_media_base_id")
            .OnTable("media_format_details")
            .OnColumn("media_base_id").Ascending()
            .WithOptions().Unique();

        Create.Table("media_streams")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("stream_type").AsString(20).NotNullable()
            .WithColumn("is_primary").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("codec_name").AsCustom("text").NotNullable()
            .WithColumn("codec_long_name").AsCustom("text").NotNullable()
            .WithColumn("bit_rate").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("bit_depth").AsInt32().Nullable()
            .WithColumn("start_time_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("language").AsString(50).Nullable();

        Create.ForeignKey("fk_media_streams_media_base_id")
            .FromTable("media_streams").ForeignColumn("media_base_id")
            .ToTable("media_base").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_media_streams_media_base_id")
            .OnTable("media_streams")
            .OnColumn("media_base_id").Ascending();

        Create.Table("video_stream_details")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_stream_id").AsInt64().NotNullable()
            .WithColumn("avg_frame_rate").AsDouble().NotNullable().WithDefaultValue(0)
            .WithColumn("bits_per_raw_sample").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("display_aspect_ratio_width").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("display_aspect_ratio_height").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("profile").AsCustom("text").NotNullable()
            .WithColumn("width").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("height").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("pixel_format").AsCustom("text").NotNullable()
            .WithColumn("rotation").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("color_space").AsCustom("text").NotNullable()
            .WithColumn("color_transfer").AsCustom("text").NotNullable()
            .WithColumn("color_primaries").AsCustom("text").NotNullable()
            .WithColumn("hdr_type").AsString(20).NotNullable().WithDefaultValue("SDR");

        Create.ForeignKey("fk_video_stream_details_media_stream_id")
            .FromTable("video_stream_details").ForeignColumn("media_stream_id")
            .ToTable("media_streams").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_video_stream_details_media_stream_id")
            .OnTable("video_stream_details")
            .OnColumn("media_stream_id").Ascending()
            .WithOptions().Unique();

        Create.Table("audio_stream_details")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_stream_id").AsInt64().NotNullable()
            .WithColumn("channels").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("channel_layout").AsCustom("text").NotNullable()
            .WithColumn("sample_rate_hz").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("profile").AsCustom("text").NotNullable();

        Create.ForeignKey("fk_audio_stream_details_media_stream_id")
            .FromTable("audio_stream_details").ForeignColumn("media_stream_id")
            .ToTable("media_streams").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_audio_stream_details_media_stream_id")
            .OnTable("audio_stream_details")
            .OnColumn("media_stream_id").Ascending()
            .WithOptions().Unique();

        Create.Table("chapter_data")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("title").AsCustom("text").NotNullable()
            .WithColumn("start_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("end_ticks").AsInt64().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("fk_chapter_data_media_base_id")
            .FromTable("chapter_data").ForeignColumn("media_base_id")
            .ToTable("media_base").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_chapter_data_media_base_id")
            .OnTable("chapter_data")
            .OnColumn("media_base_id").Ascending();

        Create.Table("media_captions")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("storage_path").AsCustom("text").NotNullable()
            .WithColumn("caption_type").AsCustom("subtitle_type_enum").NotNullable()
            .WithColumn("two_digit_language_code").AsString(10).NotNullable()
            .WithColumn("name").AsCustom("text").Nullable();

        Create.Index("ix_media_captions_media_lang_type")
            .OnTable("media_captions")
            .OnColumn("media_guid").Ascending()
            .OnColumn("two_digit_language_code").Ascending()
            .OnColumn("caption_type").Ascending();

        Create.Table("media_comments")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("comment_id").AsCustom("text").NotNullable()
            .WithColumn("parent_comment_id").AsCustom("text").Nullable()
            .WithColumn("text_comment").AsCustom("text").NotNullable()
            .WithColumn("account_id").AsInt64().NotNullable()
            .WithColumn("comment_timestamp").AsDateTime().NotNullable()
            .WithColumn("like_count").AsInt32().Nullable()
            .WithColumn("dislike_count").AsInt32().Nullable()
            .WithColumn("is_favorited").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_uploader").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_pinned").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.ForeignKey("fk_media_comments_account_id")
            .FromTable("media_comments").ForeignColumn("account_id")
            .ToTable("accounts").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_media_comments_media_time")
            .OnTable("media_comments")
            .OnColumn("media_guid").Ascending()
            .OnColumn("comment_timestamp").Ascending();

        Create.Index("ix_media_comments_comment_id")
            .OnTable("media_comments")
            .OnColumn("comment_id").Ascending();

        Create.Index("ix_media_comments_parent_id")
            .OnTable("media_comments")
            .OnColumn("parent_comment_id").Ascending();

        Create.Table("series_metadata")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("series_name").AsCustom("text").NotNullable()
            .WithColumn("season_count").AsInt32().Nullable()
            .WithColumn("season_number").AsInt32().NotNullable()
            .WithColumn("season_name").AsCustom("text").Nullable()
            .WithColumn("episode_number").AsInt32().NotNullable()
            .WithColumn("episode_name").AsCustom("text").NotNullable();

        Create.Index("ux_series_metadata_media_guid")
            .OnTable("series_metadata")
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("music_metadata")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("album_title").AsCustom("text").NotNullable()
            .WithColumn("album_type").AsCustom("text").Nullable()
            .WithColumn("disc_number").AsInt32().Nullable()
            .WithColumn("release_year").AsInt32().Nullable()
            .WithColumn("track_title").AsCustom("text").NotNullable()
            .WithColumn("track_number").AsInt32().NotNullable()
            .WithColumn("composer").AsCustom("text").Nullable();

        Create.Index("ux_music_metadata_media_guid")
            .OnTable("music_metadata")
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("artists")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("artist_name").AsCustom("text").NotNullable();

        Create.Index("ux_artists_artist_name")
            .OnTable("artists")
            .OnColumn("artist_name").Ascending()
            .WithOptions().Unique();

        Create.Table("genres")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("genre_name").AsCustom("text").NotNullable();

        Create.Index("ux_genres_genre_name")
            .OnTable("genres")
            .OnColumn("genre_name").Ascending()
            .WithOptions().Unique();

        Create.Table("tags")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("tag_name").AsCustom("text").NotNullable();

        Create.Index("ux_tags_tag_name")
            .OnTable("tags")
            .OnColumn("tag_name").Ascending()
            .WithOptions().Unique();

        Create.Table("categories")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("category_name").AsCustom("text").NotNullable();

        Create.Index("ux_categories_category_name")
            .OnTable("categories")
            .OnColumn("category_name").Ascending()
            .WithOptions().Unique();

        Create.Table("cast_members")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("cast_name").AsCustom("text").NotNullable();

        Create.Index("ux_cast_members_cast_name")
            .OnTable("cast_members")
            .OnColumn("cast_name").Ascending()
            .WithOptions().Unique();

        Create.Table("media_artists")
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("artist_id").AsInt64().NotNullable()
            .WithColumn("is_album_artist").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.PrimaryKey("pk_media_artists")
            .OnTable("media_artists")
            .Columns("media_metadata_id", "artist_id");

        Create.ForeignKey("fk_media_artists_media_metadata_id")
            .FromTable("media_artists").ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_artists_artist_id")
            .FromTable("media_artists").ForeignColumn("artist_id")
            .ToTable("artists").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_genres")
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("genre_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_genres")
            .OnTable("media_genres")
            .Columns("media_metadata_id", "genre_id");

        Create.ForeignKey("fk_media_genres_media_metadata_id")
            .FromTable("media_genres").ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_genres_genre_id")
            .FromTable("media_genres").ForeignColumn("genre_id")
            .ToTable("genres").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_tags")
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("tag_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_tags")
            .OnTable("media_tags")
            .Columns("media_metadata_id", "tag_id");

        Create.ForeignKey("fk_media_tags_media_metadata_id")
            .FromTable("media_tags").ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_tags_tag_id")
            .FromTable("media_tags").ForeignColumn("tag_id")
            .ToTable("tags").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_categories")
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("category_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_categories")
            .OnTable("media_categories")
            .Columns("media_metadata_id", "category_id");

        Create.ForeignKey("fk_media_categories_media_metadata_id")
            .FromTable("media_categories").ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_categories_category_id")
            .FromTable("media_categories").ForeignColumn("category_id")
            .ToTable("categories").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_cast")
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("cast_member_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_cast")
            .OnTable("media_cast")
            .Columns("media_metadata_id", "cast_member_id");

        Create.ForeignKey("fk_media_cast_media_metadata_id")
            .FromTable("media_cast").ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_cast_cast_member_id")
            .FromTable("media_cast").ForeignColumn("cast_member_id")
            .ToTable("cast_members").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);
    }

    public override void Down()
    {
        Delete.Table("media_cast");
        Delete.Table("media_categories");
        Delete.Table("media_tags");
        Delete.Table("media_genres");
        Delete.Table("media_artists");

        Delete.Table("cast_members");
        Delete.Table("categories");
        Delete.Table("tags");
        Delete.Table("genres");
        Delete.Table("artists");

        Delete.Table("music_metadata");
        Delete.Table("series_metadata");
        Delete.Table("media_comments");
        Delete.Table("media_captions");
        Delete.Table("chapter_data");
        Delete.Table("audio_stream_details");
        Delete.Table("video_stream_details");
        Delete.Table("media_streams");
        Delete.Table("media_format_details");
        Delete.Table("media_base");
        Delete.Table("media_metadata");
        Delete.Table("accounts");

        Execute.Sql("DROP TYPE IF EXISTS subtitle_type_enum;");
        Execute.Sql("DROP TYPE IF EXISTS availability_enum;");
    }
}
