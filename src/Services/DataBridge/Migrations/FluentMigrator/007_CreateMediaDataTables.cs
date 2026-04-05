using FluentMigrator;
using System.Data;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(7)]
public class CreateMediaDataTables : Migration
{
    public override void Up()
    {
        // ── media_base ──────────────────────────────────────────────
        // Root table that mirrors MediaBase.cs
        Create.Table("media_base")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("video_version_id").AsGuid().NotNullable()
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0);

        Create.Index("ix_media_base_video_version_id")
            .OnTable("media_base")
            .OnColumn("video_version_id")
            .Unique();

        Create.ForeignKey("fk_media_base_video_versions")
            .FromTable("media_base").ForeignColumn("video_version_id")
            .ToTable("video_versions").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        // ── media_format_details ────────────────────────────────────
        // One-to-one with media_base, mirrors MediaFormat.cs
        Create.Table("media_format_details")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("start_time_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("format_long_names").AsString().NotNullable() // stored as '/'-delimited string
            .WithColumn("stream_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("bit_rate").AsDouble().NotNullable().WithDefaultValue(0);

        Create.Index("ix_media_format_details_media_base_id")
            .OnTable("media_format_details")
            .OnColumn("media_base_id")
            .Unique();

        Create.ForeignKey("fk_media_format_details_media_base")
            .FromTable("media_format_details").ForeignColumn("media_base_id")
            .ToTable("media_base").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        // ── media_streams ───────────────────────────────────────────
        // Base stream table, mirrors MediaStream.cs shared columns.
        // Discriminator 'stream_type' = 'video' | 'audio' | 'subtitle'
        Create.Table("media_streams")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("stream_type").AsString(20).NotNullable()
            .WithColumn("is_primary").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("codec_name").AsString().NotNullable()
            .WithColumn("codec_long_name").AsString().NotNullable()
            .WithColumn("bit_rate").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("bit_depth").AsInt32().Nullable()
            .WithColumn("start_time_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("duration_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("language").AsString(50).Nullable();

        Create.Index("ix_media_streams_media_base_id")
            .OnTable("media_streams")
            .OnColumn("media_base_id");

        Create.ForeignKey("fk_media_streams_media_base")
            .FromTable("media_streams").ForeignColumn("media_base_id")
            .ToTable("media_base").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        // ── video_stream_details ────────────────────────────────────
        // One-to-one extension of a media_stream row where stream_type='video'
        // Mirrors VideoStream.cs
        Create.Table("video_stream_details")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_stream_id").AsInt64().NotNullable()
            .WithColumn("avg_frame_rate").AsDouble().NotNullable().WithDefaultValue(0)
            .WithColumn("bits_per_raw_sample").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("display_aspect_ratio_width").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("display_aspect_ratio_height").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("profile").AsString().NotNullable()
            .WithColumn("width").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("height").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("pixel_format").AsString().NotNullable()
            .WithColumn("rotation").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("color_space").AsString().NotNullable()
            .WithColumn("color_transfer").AsString().NotNullable()
            .WithColumn("color_primaries").AsString().NotNullable()
            .WithColumn("hdr_type").AsString(20).NotNullable().WithDefaultValue("SDR");

        Create.Index("ix_video_stream_details_media_stream_id")
            .OnTable("video_stream_details")
            .OnColumn("media_stream_id")
            .Unique();

        Create.ForeignKey("fk_video_stream_details_media_streams")
            .FromTable("video_stream_details").ForeignColumn("media_stream_id")
            .ToTable("media_streams").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        // ── audio_stream_details ────────────────────────────────────
        // One-to-one extension of a media_stream row where stream_type='audio'
        // Mirrors AudioStream.cs
        Create.Table("audio_stream_details")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_stream_id").AsInt64().NotNullable()
            .WithColumn("channels").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("channel_layout").AsString().NotNullable()
            .WithColumn("sample_rate_hz").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("profile").AsString().NotNullable();

        Create.Index("ix_audio_stream_details_media_stream_id")
            .OnTable("audio_stream_details")
            .OnColumn("media_stream_id")
            .Unique();

        Create.ForeignKey("fk_audio_stream_details_media_streams")
            .FromTable("audio_stream_details").ForeignColumn("media_stream_id")
            .ToTable("media_streams").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        // ── chapter_data ────────────────────────────────────────────
        // Many chapters per media_base, mirrors ChapterData.cs
        Create.Table("chapter_data")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_base_id").AsInt64().NotNullable()
            .WithColumn("title").AsString().NotNullable()
            .WithColumn("start_ticks").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("end_ticks").AsInt64().NotNullable().WithDefaultValue(0);

        Create.Index("ix_chapter_data_media_base_id")
            .OnTable("chapter_data")
            .OnColumn("media_base_id");

        Create.ForeignKey("fk_chapter_data_media_base")
            .FromTable("chapter_data").ForeignColumn("media_base_id")
            .ToTable("media_base").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);
    }

    public override void Down()
    {
        // Drop in reverse dependency order
        Delete.Table("chapter_data");
        Delete.Table("audio_stream_details");
        Delete.Table("video_stream_details");
        Delete.Table("media_streams");
        Delete.Table("media_format_details");
        Delete.Table("media_base");
    }
}
