using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(29, "Add meta_storage_path column to download_jobs")]
public sealed class M029_AddMetaStoragePath : Migration
{
    public override void Up()
    {
        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("meta_storage_path").AsString(2048).Nullable();
    }

    public override void Down()
    {
        Delete.Column("meta_storage_path")
            .FromTable("download_jobs").InSchema("downloads");
    }
}
