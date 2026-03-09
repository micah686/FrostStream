using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(4)]
public class AddVideoVersionVariantColumns : Migration
{
    public override void Up()
    {
        // Add new columns for versioned storage support
        Alter.Table("video_versions")
            .AddColumn("media_type").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("variant_type").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("quality").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("source_version_id").AsGuid().Nullable()
            .AddColumn("codec").AsString(20).Nullable()
            .AddColumn("file_size").AsInt64().NotNullable().WithDefaultValue(0);

        // Create index for efficient querying by media type and quality
        Create.Index("ix_video_versions_media_quality")
            .OnTable("video_versions")
            .OnColumn("video_id").Ascending()
            .OnColumn("media_type").Ascending()
            .OnColumn("quality").Ascending();

        // Create index for source version lookups (for transcoded variants)
        Create.Index("ix_video_versions_source_version_id")
            .OnTable("video_versions")
            .OnColumn("source_version_id");
    }

    public override void Down()
    {
        Delete.Index("ix_video_versions_source_version_id").OnTable("video_versions");
        Delete.Index("ix_video_versions_media_quality").OnTable("video_versions");

        Delete.Column("file_size").FromTable("video_versions");
        Delete.Column("codec").FromTable("video_versions");
        Delete.Column("source_version_id").FromTable("video_versions");
        Delete.Column("quality").FromTable("video_versions");
        Delete.Column("variant_type").FromTable("video_versions");
        Delete.Column("media_type").FromTable("video_versions");
    }
}
