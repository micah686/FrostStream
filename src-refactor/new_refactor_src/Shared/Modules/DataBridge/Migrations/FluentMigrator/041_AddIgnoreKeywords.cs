using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(41, "Add ignore keywords to config sets and ignored-keyword reporting columns")]
public sealed class M041_AddIgnoreKeywords : Migration
{
    public override void Up()
    {
        Alter.Table("download_config_sets").InSchema("downloads")
            .AddColumn("ignore_keywords_json").AsCustom("jsonb").Nullable();

        Alter.Table("discovered_media").InSchema("discovery")
            .AddColumn("ignored_keyword").AsString(200).Nullable();

        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("ignored_keyword").AsString(200).Nullable();
    }

    public override void Down()
    {
        Delete.Column("ignored_keyword").FromTable("download_jobs").InSchema("downloads");
        Delete.Column("ignored_keyword").FromTable("discovered_media").InSchema("discovery");
        Delete.Column("ignore_keywords_json").FromTable("download_config_sets").InSchema("downloads");
    }
}
