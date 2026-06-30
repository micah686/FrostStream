using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(15, "Add info.json sidecar columns to download_jobs")]
public sealed class M015_AddDownloadJobsInfoJsonColumns : Migration
{
    public override void Up()
    {
        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("info_json_storage_path").AsString(2048).Nullable()
            .AddColumn("info_json_content_hash_xxh128").AsString(64).Nullable()
            .AddColumn("info_json_size_bytes").AsInt64().Nullable();
    }

    public override void Down()
    {
        Delete.Column("info_json_storage_path")
            .Column("info_json_content_hash_xxh128")
            .Column("info_json_size_bytes")
            .FromTable("download_jobs").InSchema("downloads");
    }
}
