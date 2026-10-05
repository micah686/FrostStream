using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(7, "Create media.media root table and wire media_guid foreign keys from version + metadata tables")]
public sealed class M007_CreateMediaRootTable : Migration
{
    private const string SchemaName = "media";
    private const string MetadataSchema = "metadata";

    public override void Up()
    {
        Create.Table("media").InSchema(SchemaName)
            .WithColumn("media_guid").AsCustom("uuid").PrimaryKey()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Execute.Sql(
            """
            INSERT INTO media.media (media_guid)
            SELECT DISTINCT media_guid FROM (
                SELECT media_guid FROM media.media_source_versions
                UNION
                SELECT media_guid FROM media.media_content_id_versions
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
            .FromTable("media_source_versions").InSchema(SchemaName).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_content_id_versions_media_guid")
            .FromTable("media_content_id_versions").InSchema(SchemaName).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_metadata_media_guid")
            .FromTable("media_metadata").InSchema(MetadataSchema).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_base_media_guid")
            .FromTable("media_base").InSchema(MetadataSchema).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_captions_media_guid")
            .FromTable("media_captions").InSchema(MetadataSchema).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_media_comments_media_guid")
            .FromTable("media_comments").InSchema(MetadataSchema).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_series_metadata_media_guid")
            .FromTable("series_metadata").InSchema(MetadataSchema).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_metadata_music_metadata_media_guid")
            .FromTable("music_metadata").InSchema(MetadataSchema).ForeignColumn("media_guid")
            .ToTable("media").InSchema(SchemaName).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);
    }

    public override void Down()
    {
        Delete.ForeignKey("fk_metadata_music_metadata_media_guid").OnTable("music_metadata").InSchema(MetadataSchema);
        Delete.ForeignKey("fk_metadata_series_metadata_media_guid").OnTable("series_metadata").InSchema(MetadataSchema);
        Delete.ForeignKey("fk_metadata_media_comments_media_guid").OnTable("media_comments").InSchema(MetadataSchema);
        Delete.ForeignKey("fk_metadata_media_captions_media_guid").OnTable("media_captions").InSchema(MetadataSchema);
        Delete.ForeignKey("fk_metadata_media_base_media_guid").OnTable("media_base").InSchema(MetadataSchema);
        Delete.ForeignKey("fk_metadata_media_metadata_media_guid").OnTable("media_metadata").InSchema(MetadataSchema);
        Delete.ForeignKey("fk_media_content_id_versions_media_guid").OnTable("media_content_id_versions").InSchema(SchemaName);
        Delete.ForeignKey("fk_media_source_versions_media_guid").OnTable("media_source_versions").InSchema(SchemaName);

        Delete.Table("media").InSchema(SchemaName);
    }
}
