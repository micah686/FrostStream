using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(3, "Create video formats, thumbnails, and captions tables")]
public class M003_CreateVideoComponents : Migration
{
    public override void Up()
    {
        // Video formats table
        Create.Table("video_formats")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("format_id").AsString(100).NotNullable()
            .WithColumn("format_note").AsString(255).Nullable()
            .WithColumn("ext").AsString(20).Nullable()
            .WithColumn("protocol").AsString(50).Nullable()
            .WithColumn("vcodec").AsString(100).Nullable()
            .WithColumn("acodec").AsString(100).Nullable()
            .WithColumn("dynamic_range").AsString(20).Nullable()
            .WithColumn("width").AsInt32().Nullable()
            .WithColumn("height").AsInt32().Nullable()
            .WithColumn("fps").AsDecimal(5, 2).Nullable()
            .WithColumn("aspect_ratio").AsDecimal(4, 2).Nullable()
            .WithColumn("quality").AsString(50).Nullable()
            .WithColumn("quality_score").AsDecimal(5, 2).Nullable()
            .WithColumn("vbr").AsDecimal(10, 3).Nullable()
            .WithColumn("abr").AsDecimal(10, 3).Nullable()
            .WithColumn("tbr").AsDecimal(10, 3).Nullable()
            .WithColumn("asr").AsInt32().Nullable()
            .WithColumn("audio_channels").AsInt32().Nullable()
            .WithColumn("filesize").AsInt64().Nullable()
            .WithColumn("filesize_approx").AsInt64().Nullable()
            .WithColumn("url").AsString().Nullable()
            .WithColumn("manifest_url").AsString().Nullable()
            .WithColumn("http_headers").AsCustom("JSONB").Nullable()
            .WithColumn("is_available").AsBoolean().WithDefaultValue(true)
            .WithColumn("expires_at").AsDateTimeOffset().Nullable()
            .WithColumn("raw_data").AsCustom("JSONB").Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Create.UniqueConstraint("idx_formats_video_format")
            .OnTable("video_formats")
            .Columns("video_id", "format_id");

        Execute.Sql("CREATE INDEX idx_formats_video ON video_formats(video_id);");
        Execute.Sql("CREATE INDEX idx_formats_quality ON video_formats(quality);");
        Execute.Sql("CREATE INDEX idx_formats_available ON video_formats(is_available, expires_at);");

        // Thumbnails table
        Create.Table("video_thumbnails")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("url").AsString().NotNullable()
            .WithColumn("width").AsInt32().Nullable()
            .WithColumn("height").AsInt32().Nullable()
            .WithColumn("resolution").AsString(50).Nullable()
            .WithColumn("preference").AsInt32().WithDefaultValue(0)
            .WithColumn("external_id").AsString(100).Nullable()
            .WithColumn("local_path").AsString().Nullable()
            .WithColumn("downloaded_at").AsDateTimeOffset().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Execute.Sql("CREATE INDEX idx_thumbnails_video ON video_thumbnails(video_id);");
        Execute.Sql("CREATE INDEX idx_thumbnails_preference ON video_thumbnails(video_id, preference);");

        // Captions table
        Create.Table("video_captions")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("language_code").AsString(10).NotNullable()
            .WithColumn("language_name").AsString(100).Nullable()
            .WithColumn("caption_type").AsString(50).Nullable()
            .WithColumn("format").AsString(20).Nullable()
            .WithColumn("url").AsString().Nullable()
            .WithColumn("local_path").AsString().Nullable()
            .WithColumn("downloaded_at").AsDateTimeOffset().Nullable()
            .WithColumn("raw_data").AsCustom("JSONB").Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Create.UniqueConstraint("idx_captions_video_lang_type")
            .OnTable("video_captions")
            .Columns("video_id", "language_code", "caption_type");

        Execute.Sql("CREATE INDEX idx_captions_video ON video_captions(video_id);");
        Execute.Sql("CREATE INDEX idx_captions_language ON video_captions(language_code);");
    }

    public override void Down()
    {
        Delete.Table("video_captions");
        Delete.Table("video_thumbnails");
        Delete.Table("video_formats");
    }
}
