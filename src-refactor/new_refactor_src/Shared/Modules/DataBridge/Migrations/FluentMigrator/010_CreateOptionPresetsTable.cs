using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(10, "Create download_option_presets table for stored yt-dlp option sets")]
public sealed class M010_CreateOptionPresetsTable : Migration
{
    private const string SchemaName = "downloads";

    public override void Up()
    {
        Create.Table("download_option_presets").InSchema(SchemaName)
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("key").AsString(100).NotNullable()
            .WithColumn("name").AsString(255).NotNullable()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("ytdlp_options_json").AsCustom("jsonb").NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Create.UniqueConstraint("uq_download_option_presets_key")
            .OnTable("download_option_presets").WithSchema(SchemaName)
            .Column("key");

        Execute.Sql(
            "ALTER TABLE downloads.download_option_presets ADD CONSTRAINT ck_download_option_presets_key_format " +
            "CHECK (\"key\" ~ '^[a-z0-9-]{2,100}$');");
    }

    public override void Down()
    {
        Delete.Table("download_option_presets").InSchema(SchemaName);
    }
}
