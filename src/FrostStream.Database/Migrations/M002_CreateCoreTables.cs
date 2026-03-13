using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(2, "Create platforms, channels, and videos tables")]
public class M002_CreateCoreTables : Migration
{
    public override void Up()
    {
        // Platforms table
        Create.Table("platforms")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("name").AsString(100).NotNullable().Unique()
            .WithColumn("display_name").AsString(200).NotNullable()
            .WithColumn("domain").AsString(255).Nullable()
            .WithColumn("icon_url").AsString().Nullable()
            .WithColumn("config").AsCustom("JSONB").WithDefaultValue("{}")
            .WithColumn("is_enabled").AsBoolean().WithDefaultValue(true)
            .WithColumn("supports_live").AsBoolean().WithDefaultValue(false)
            .WithColumn("supports_captions").AsBoolean().WithDefaultValue(false)
            .WithColumn("supports_heatmaps").AsBoolean().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        // Channels table
        Create.Table("channels")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("platform_id").AsGuid().NotNullable().ForeignKey("platforms", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("external_id").AsString(255).NotNullable()
            .WithColumn("name").AsString(500).NotNullable()
            .WithColumn("display_name").AsString(500).Nullable()
            .WithColumn("description").AsString(int.MaxValue).Nullable()
            .WithColumn("url").AsString().Nullable()
            .WithColumn("thumbnail_url").AsString().Nullable()
            .WithColumn("banner_url").AsString().Nullable()
            .WithColumn("follower_count").AsInt64().Nullable()
            .WithColumn("metadata").AsCustom("JSONB").WithDefaultValue("{}")
            .WithColumn("is_verified").AsBoolean().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        // Unique constraint for platform + external_id
        Create.UniqueConstraint("idx_channels_platform_external")
            .OnTable("channels")
            .Columns("platform_id", "external_id");

        // Videos table
        Create.Table("videos")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("platform_id").AsGuid().NotNullable().ForeignKey("platforms", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("channel_id").AsGuid().Nullable().ForeignKey("channels", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("external_id").AsString(255).NotNullable()
            .WithColumn("title").AsString(500).NotNullable()
            .WithColumn("description").AsString(int.MaxValue).Nullable()
            .WithColumn("duration").AsInt32().Nullable()
            .WithColumn("view_count").AsInt64().WithDefaultValue(0)
            .WithColumn("like_count").AsInt64().WithDefaultValue(0)
            .WithColumn("dislike_count").AsInt64().WithDefaultValue(0)
            .WithColumn("comment_count").AsInt64().WithDefaultValue(0)
            .WithColumn("uploaded_at").AsDateTimeOffset().Nullable()
            .WithColumn("release_date").AsDate().Nullable()
            .WithColumn("release_year").AsInt32().Nullable()
            .WithColumn("age_limit").AsInt32().WithDefaultValue(0)
            .WithColumn("language").AsString(10).Nullable()
            .WithColumn("is_live").AsBoolean().WithDefaultValue(false)
            .WithColumn("was_live").AsBoolean().WithDefaultValue(false)
            .WithColumn("live_status").AsString(50).Nullable()
            .WithColumn("availability").AsString(50).WithDefaultValue("public")
            .WithColumn("webpage_url").AsString().Nullable()
            .WithColumn("webpage_html").AsString(int.MaxValue).Nullable()
            .WithColumn("webpage_captured_at").AsDateTimeOffset().Nullable()
            .WithColumn("search_vector").AsCustom("TSVECTOR").Nullable()
            .WithColumn("is_deleted").AsBoolean().WithDefaultValue(false)
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_reason").AsString(100).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("last_metadata_sync_at").AsDateTimeOffset().Nullable();

        // Unique constraint for platform + external_id on videos
        Create.UniqueConstraint("idx_videos_platform_external")
            .OnTable("videos")
            .Columns("platform_id", "external_id");

        // Indexes
        Execute.Sql("CREATE INDEX idx_channels_name ON channels USING gin(name gin_trgm_ops);");
        Execute.Sql("CREATE INDEX idx_videos_channel ON videos(channel_id);");
        Execute.Sql("CREATE INDEX idx_videos_uploaded_at ON videos(uploaded_at DESC);");
        Execute.Sql("CREATE INDEX idx_videos_search ON videos USING gin(search_vector);");
        Execute.Sql("CREATE INDEX idx_videos_title_trgm ON videos USING gin(title gin_trgm_ops);");
        Execute.Sql("CREATE INDEX idx_videos_live ON videos(is_live, live_status) WHERE is_live = true;");
        Execute.Sql("CREATE INDEX idx_videos_deleted ON videos(is_deleted) WHERE is_deleted = false;");
        Execute.Sql("CREATE INDEX idx_videos_metadata_sync ON videos(last_metadata_sync_at);");
    }

    public override void Down()
    {
        Delete.Table("videos");
        Delete.Table("channels");
        Delete.Table("platforms");
    }
}
