using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(6)]
public class CreateMediaFormatsAndRemoveObsoleteColumns : Migration
{
    public override void Up()
    {
        // Create media_formats table
        Create.Table("media_formats")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("video_version_id").AsGuid().NotNullable()
            .WithColumn("file_size").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("average_bit_rate").AsDouble().Nullable()
            .WithColumn("audio_bitrate").AsDouble().Nullable()
            .WithColumn("audio_sampling_rate").AsDouble().Nullable()
            .WithColumn("audio_channels").AsInt16().Nullable()
            .WithColumn("audio_codec").AsString().Nullable()
            .WithColumn("width").AsInt32().Nullable()
            .WithColumn("height").AsInt32().Nullable()
            .WithColumn("aspect_ratio").AsString().Nullable()
            .WithColumn("video_bitrate").AsDouble().Nullable()
            .WithColumn("frame_rate").AsFloat().Nullable()
            .WithColumn("video_codec").AsString().Nullable()
            .WithColumn("dynamic_range").AsString().Nullable()
            .WithColumn("friendly_video_resolution").AsString().Nullable();

        // Create unique index on video_version_id for one-to-one relationship
        Create.Index("ix_media_formats_video_version_id")
            .OnTable("media_formats")
            .OnColumn("video_version_id")
            .Unique();

        // Add foreign key constraint
        Create.ForeignKey("fk_media_formats_video_versions")
            .FromTable("media_formats")
            .ForeignColumn("video_version_id")
            .ToTable("video_versions")
            .PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        // Remove obsolete columns from video_versions
        Delete.Index("ix_video_versions_media_quality").OnTable("video_versions");
        
        Delete.Column("file_size").FromTable("video_versions");
        Delete.Column("codec").FromTable("video_versions");
        Delete.Column("quality").FromTable("video_versions");
        Delete.Column("variant_type").FromTable("video_versions");
        Delete.Column("media_type").FromTable("video_versions");
    }

    public override void Down()
    {
        // Add back the obsolete columns
        Alter.Table("video_versions")
            .AddColumn("media_type").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("variant_type").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("quality").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("codec").AsString(20).Nullable()
            .AddColumn("file_size").AsInt64().NotNullable().WithDefaultValue(0);

        // Recreate the index
        Create.Index("ix_video_versions_media_quality")
            .OnTable("video_versions")
            .OnColumn("video_id").Ascending()
            .OnColumn("media_type").Ascending()
            .OnColumn("quality").Ascending();

        // Drop media_formats table
        Delete.ForeignKey("fk_media_formats_video_versions").OnTable("media_formats");
        Delete.Index("ix_media_formats_video_version_id").OnTable("media_formats");
        Delete.Table("media_formats");
    }
}
