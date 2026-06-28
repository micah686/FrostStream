using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(37, "Create user-scoped download configuration sets")]
public sealed class M037_CreateDownloadConfigSets : Migration
{
    public override void Up()
    {
        Create.Table("download_config_sets").InSchema("downloads")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("owner_subject").AsString(255).NotNullable()
            .WithColumn("key").AsString(100).NotNullable()
            .WithColumn("name").AsString(255).NotNullable()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("storage_key").AsString(100).Nullable()
            .WithColumn("cookie_profile_key").AsString(100).Nullable()
            .WithColumn("ytdlp_options_json").AsCustom("jsonb").Nullable()
            .WithColumn("encode_for_playlist").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("audio_format").AsCustom("media.audio_rendition_format").NotNullable().WithDefaultValue("aac")
            .WithColumn("priority").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("fetch_comments").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.UniqueConstraint("uq_download_config_sets_owner_key")
            .OnTable("download_config_sets").WithSchema("downloads")
            .Columns("owner_subject", "key");

        Execute.Sql(
            "ALTER TABLE downloads.download_config_sets ADD CONSTRAINT ck_download_config_sets_key_format " +
            "CHECK (\"key\" ~ '^[a-z0-9-]{2,100}$');");
    }

    public override void Down()
    {
        Delete.Table("download_config_sets").InSchema("downloads");
    }
}
