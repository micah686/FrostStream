using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(7, "Create public.media root table and wire media_guid foreign keys from version + metadata tables")]
public sealed class M007_CreateMediaRootTable : Migration
{
    public override void Up()
    {
        Create.Table("media")
            .WithColumn("media_guid").AsCustom("uuid").PrimaryKey()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Execute.Sql(
            """
            INSERT INTO media (media_guid)
            SELECT DISTINCT media_guid FROM (
                SELECT media_guid FROM media_source_versions
                UNION
                SELECT media_guid FROM media_content_id_versions
                UNION
                SELECT media_guid FROM metadata.media_metadata
                UNION
                SELECT media_guid FROM metadata.media_base
                UNION
                SELECT media_guid FROM metadata.media_captions
                UNION
                SELECT media_guid FROM metadata.media_comments
                UNION
                SELECT media_guid FROM metadata.series_metadata
                UNION
                SELECT media_guid FROM metadata.music_metadata
            ) AS all_guids;
            """);

        Create.ForeignKey("fk_media_source_versions_media_guid")
            .FromTable("media_source_versions").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_content_id_versions_media_guid")
            .FromTable("media_content_id_versions").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_metadata_media_guid")
            .FromTable("media_metadata").InSchema("metadata").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_base_media_guid")
            .FromTable("media_base").InSchema("metadata").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_captions_media_guid")
            .FromTable("media_captions").InSchema("metadata").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_comments_media_guid")
            .FromTable("media_comments").InSchema("metadata").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_series_metadata_media_guid")
            .FromTable("series_metadata").InSchema("metadata").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_music_metadata_media_guid")
            .FromTable("music_metadata").InSchema("metadata").ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);
    }

    public override void Down()
    {
        Delete.ForeignKey("fk_metadata_music_metadata_media_guid").OnTable("music_metadata").InSchema("metadata");
        Delete.ForeignKey("fk_metadata_series_metadata_media_guid").OnTable("series_metadata").InSchema("metadata");
        Delete.ForeignKey("fk_metadata_media_comments_media_guid").OnTable("media_comments").InSchema("metadata");
        Delete.ForeignKey("fk_metadata_media_captions_media_guid").OnTable("media_captions").InSchema("metadata");
        Delete.ForeignKey("fk_metadata_media_base_media_guid").OnTable("media_base").InSchema("metadata");
        Delete.ForeignKey("fk_metadata_media_metadata_media_guid").OnTable("media_metadata").InSchema("metadata");
        Delete.ForeignKey("fk_media_content_id_versions_media_guid").OnTable("media_content_id_versions");
        Delete.ForeignKey("fk_media_source_versions_media_guid").OnTable("media_source_versions");

        Delete.Table("media");
    }
}
