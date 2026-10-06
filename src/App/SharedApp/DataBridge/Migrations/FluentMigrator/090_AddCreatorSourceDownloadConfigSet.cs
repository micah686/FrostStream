using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(90, "Link creator sources to download config sets")]
public sealed class M090_AddCreatorSourceDownloadConfigSet : Migration
{
    public override void Up()
    {
        Alter.Table("creator_sources").InSchema("discovery")
            .AddColumn("config_set_owner_subject").AsString(255).Nullable()
            .AddColumn("config_set_key").AsString(100).Nullable();
    }

    public override void Down()
    {
        Delete.Column("config_set_key").FromTable("creator_sources").InSchema("discovery");
        Delete.Column("config_set_owner_subject").FromTable("creator_sources").InSchema("discovery");
    }
}
