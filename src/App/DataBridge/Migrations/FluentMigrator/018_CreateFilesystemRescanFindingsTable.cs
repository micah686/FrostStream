using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(18, "Create filesystem_rescan_findings table")]
public sealed class M018_CreateFilesystemRescanFindingsTable : Migration
{
    public override void Up()
    {
        Create.Table("filesystem_rescan_findings")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("storage_path").AsString(2048).NotNullable()
            .WithColumn("finding_type").AsString(32).NotNullable()
            .WithColumn("media_guid").AsCustom("uuid").Nullable()
            .WithColumn("detected_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("last_seen_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("resolved_at").AsCustom("timestamp with time zone").Nullable();

        Create.Index("uq_filesystem_rescan_findings_key_path_type")
            .OnTable("filesystem_rescan_findings")
            .OnColumn("storage_key").Ascending()
            .OnColumn("storage_path").Ascending()
            .OnColumn("finding_type").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("filesystem_rescan_findings");
    }
}
