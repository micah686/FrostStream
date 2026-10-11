using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(6, "Create metadata schema with accounts, media metadata, technical detail, taxonomy, and junction tables")]
public sealed class M006_CreateMediaMetadataSchema : Migration
{
    private const string SchemaName = "metadata";

    public override void Up()
    {
        Create.Schema(SchemaName);

        Execute.Sql(
            "CREATE TYPE metadata.availability_enum AS ENUM (" +
            "'unknown','private','premium_only','subscriber_only','needs_auth','unlisted','public');");

        Execute.Sql(
            "CREATE TYPE metadata.subtitle_type_enum AS ENUM (" +
            "'subtitles','automatic_captions');");

        Create.Table("accounts").InSchema(SchemaName)
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
            .OnTable("accounts").InSchema(SchemaName)
            .OnColumn("platform").Ascending()
            .OnColumn("account_handle").Ascending()
            .WithOptions().Unique();

        Create.Table("media_metadata").InSchema(SchemaName)
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
            .WithColumn("availability").AsCustom("metadata.availability_enum").Nullable()
            .WithColumn("location").AsCustom("text").Nullable();

        Create.ForeignKey("fk_media_metadata_account_id")
            .FromTable("media_metadata").InSchema(SchemaName).ForeignColumn("account_id")
            .ToTable("accounts").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ux_media_metadata_media_guid")
            .OnTable("media_metadata").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("media_base").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0);

        Create.Index("ix_media_base_media_guid")
            .OnTable("media_base").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("media_format_details").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("start_time_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("format_long_names").AsCustom("text").NotNullable()
            .WithColumn("stream_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("bit_rate").AsDouble().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("fk_media_format_details_media_base_id")
            .FromTable("media_format_details").InSchema(SchemaName).ForeignColumn("media_base_id")
            .ToTable("media_base").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_media_format_details_media_base_id")
            .OnTable("media_format_details").InSchema(SchemaName)
            .OnColumn("media_base_id").Ascending()
            .WithOptions().Unique();

        Create.Table("media_streams").InSchema(SchemaName)
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
            .FromTable("media_streams").InSchema(SchemaName).ForeignColumn("media_base_id")
            .ToTable("media_base").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_media_streams_media_base_id")
            .OnTable("media_streams").InSchema(SchemaName)
            .OnColumn("media_base_id").Ascending();

        Create.Table("video_stream_details").InSchema(SchemaName)
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
            .FromTable("video_stream_details").InSchema(SchemaName).ForeignColumn("media_stream_id")
            .ToTable("media_streams").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_video_stream_details_media_stream_id")
            .OnTable("video_stream_details").InSchema(SchemaName)
            .OnColumn("media_stream_id").Ascending()
            .WithOptions().Unique();

        Create.Table("audio_stream_details").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_stream_id").AsInt64().NotNullable()
            .WithColumn("channels").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("channel_layout").AsCustom("text").NotNullable()
            .WithColumn("sample_rate_hz").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("profile").AsCustom("text").NotNullable();

        Create.ForeignKey("fk_audio_stream_details_media_stream_id")
            .FromTable("audio_stream_details").InSchema(SchemaName).ForeignColumn("media_stream_id")
            .ToTable("media_streams").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_audio_stream_details_media_stream_id")
            .OnTable("audio_stream_details").InSchema(SchemaName)
            .OnColumn("media_stream_id").Ascending()
            .WithOptions().Unique();

        Create.Table("chapter_data").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("title").AsCustom("text").NotNullable()
            .WithColumn("start_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("end_ticks").AsInt64().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("fk_chapter_data_media_base_id")
            .FromTable("chapter_data").InSchema(SchemaName).ForeignColumn("media_base_id")
            .ToTable("media_base").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_chapter_data_media_base_id")
            .OnTable("chapter_data").InSchema(SchemaName)
            .OnColumn("media_base_id").Ascending();

        Create.Table("media_captions").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("storage_path").AsCustom("text").NotNullable()
            .WithColumn("caption_type").AsCustom("metadata.subtitle_type_enum").NotNullable()
            .WithColumn("two_digit_language_code").AsString(10).NotNullable()
            .WithColumn("name").AsCustom("text").Nullable();

        Create.Index("ix_media_captions_media_lang_type")
            .OnTable("media_captions").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .OnColumn("two_digit_language_code").Ascending()
            .OnColumn("caption_type").Ascending();

        Create.Table("media_comments").InSchema(SchemaName)
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
            .FromTable("media_comments").InSchema(SchemaName).ForeignColumn("account_id")
            .ToTable("accounts").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_media_comments_media_time")
            .OnTable("media_comments").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .OnColumn("comment_timestamp").Ascending();

        Create.Index("ix_media_comments_comment_id")
            .OnTable("media_comments").InSchema(SchemaName)
            .OnColumn("comment_id").Ascending();

        Create.Index("ix_media_comments_parent_id")
            .OnTable("media_comments").InSchema(SchemaName)
            .OnColumn("parent_comment_id").Ascending();

        Create.Table("series_metadata").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("series_name").AsCustom("text").NotNullable()
            .WithColumn("season_count").AsInt32().Nullable()
            .WithColumn("season_number").AsInt32().NotNullable()
            .WithColumn("season_name").AsCustom("text").Nullable()
            .WithColumn("episode_number").AsInt32().NotNullable()
            .WithColumn("episode_name").AsCustom("text").NotNullable();

        Create.Index("ux_series_metadata_media_guid")
            .OnTable("series_metadata").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("music_metadata").InSchema(SchemaName)
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
            .OnTable("music_metadata").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .WithOptions().Unique();

        Create.Table("artists").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("artist_name").AsCustom("text").NotNullable();

        Create.Index("ux_artists_artist_name")
            .OnTable("artists").InSchema(SchemaName)
            .OnColumn("artist_name").Ascending()
            .WithOptions().Unique();

        Create.Table("genres").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("genre_name").AsCustom("text").NotNullable();

        Create.Index("ux_genres_genre_name")
            .OnTable("genres").InSchema(SchemaName)
            .OnColumn("genre_name").Ascending()
            .WithOptions().Unique();

        Create.Table("tags").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("tag_name").AsCustom("text").NotNullable();

        Create.Index("ux_tags_tag_name")
            .OnTable("tags").InSchema(SchemaName)
            .OnColumn("tag_name").Ascending()
            .WithOptions().Unique();

        Create.Table("categories").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("category_name").AsCustom("text").NotNullable();

        Create.Index("ux_categories_category_name")
            .OnTable("categories").InSchema(SchemaName)
            .OnColumn("category_name").Ascending()
            .WithOptions().Unique();

        Create.Table("cast_members").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("cast_name").AsCustom("text").NotNullable();

        Create.Index("ux_cast_members_cast_name")
            .OnTable("cast_members").InSchema(SchemaName)
            .OnColumn("cast_name").Ascending()
            .WithOptions().Unique();

        Create.Table("media_artists").InSchema(SchemaName)
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("artist_id").AsInt64().NotNullable()
            .WithColumn("is_album_artist").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.PrimaryKey("pk_media_artists")
            .OnTable("media_artists").WithSchema(SchemaName)
            .Columns("media_metadata_id", "artist_id");

        Create.ForeignKey("fk_media_artists_media_metadata_id")
            .FromTable("media_artists").InSchema(SchemaName).ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_artists_artist_id")
            .FromTable("media_artists").InSchema(SchemaName).ForeignColumn("artist_id")
            .ToTable("artists").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_genres").InSchema(SchemaName)
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("genre_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_genres")
            .OnTable("media_genres").WithSchema(SchemaName)
            .Columns("media_metadata_id", "genre_id");

        Create.ForeignKey("fk_media_genres_media_metadata_id")
            .FromTable("media_genres").InSchema(SchemaName).ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_genres_genre_id")
            .FromTable("media_genres").InSchema(SchemaName).ForeignColumn("genre_id")
            .ToTable("genres").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_tags").InSchema(SchemaName)
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("tag_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_tags")
            .OnTable("media_tags").WithSchema(SchemaName)
            .Columns("media_metadata_id", "tag_id");

        Create.ForeignKey("fk_media_tags_media_metadata_id")
            .FromTable("media_tags").InSchema(SchemaName).ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_tags_tag_id")
            .FromTable("media_tags").InSchema(SchemaName).ForeignColumn("tag_id")
            .ToTable("tags").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_categories").InSchema(SchemaName)
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("category_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_categories")
            .OnTable("media_categories").WithSchema(SchemaName)
            .Columns("media_metadata_id", "category_id");

        Create.ForeignKey("fk_media_categories_media_metadata_id")
            .FromTable("media_categories").InSchema(SchemaName).ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_categories_category_id")
            .FromTable("media_categories").InSchema(SchemaName).ForeignColumn("category_id")
            .ToTable("categories").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Table("media_cast").InSchema(SchemaName)
            .WithColumn("media_metadata_id").AsInt64().NotNullable()
            .WithColumn("cast_member_id").AsInt64().NotNullable();

        Create.PrimaryKey("pk_media_cast")
            .OnTable("media_cast").WithSchema(SchemaName)
            .Columns("media_metadata_id", "cast_member_id");

        Create.ForeignKey("fk_media_cast_media_metadata_id")
            .FromTable("media_cast").InSchema(SchemaName).ForeignColumn("media_metadata_id")
            .ToTable("media_metadata").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_cast_cast_member_id")
            .FromTable("media_cast").InSchema(SchemaName).ForeignColumn("cast_member_id")
            .ToTable("cast_members").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);
    }

    public override void Down()
    {
        Delete.Table("media_cast").InSchema(SchemaName);
        Delete.Table("media_categories").InSchema(SchemaName);
        Delete.Table("media_tags").InSchema(SchemaName);
        Delete.Table("media_genres").InSchema(SchemaName);
        Delete.Table("media_artists").InSchema(SchemaName);

        Delete.Table("cast_members").InSchema(SchemaName);
        Delete.Table("categories").InSchema(SchemaName);
        Delete.Table("tags").InSchema(SchemaName);
        Delete.Table("genres").InSchema(SchemaName);
        Delete.Table("artists").InSchema(SchemaName);

        Delete.Table("music_metadata").InSchema(SchemaName);
        Delete.Table("series_metadata").InSchema(SchemaName);
        Delete.Table("media_comments").InSchema(SchemaName);
        Delete.Table("media_captions").InSchema(SchemaName);
        Delete.Table("chapter_data").InSchema(SchemaName);
        Delete.Table("audio_stream_details").InSchema(SchemaName);
        Delete.Table("video_stream_details").InSchema(SchemaName);
        Delete.Table("media_streams").InSchema(SchemaName);
        Delete.Table("media_format_details").InSchema(SchemaName);
        Delete.Table("media_base").InSchema(SchemaName);
        Delete.Table("media_metadata").InSchema(SchemaName);
        Delete.Table("accounts").InSchema(SchemaName);

        Execute.Sql("DROP TYPE IF EXISTS metadata.subtitle_type_enum;");
        Execute.Sql("DROP TYPE IF EXISTS metadata.availability_enum;");

        Delete.Schema(SchemaName);
    }
}
