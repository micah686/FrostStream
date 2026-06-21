using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(22, "Add creator source full scan cursor")]
public sealed class M022_AddCreatorSourceFullScanCursor : Migration
{
    public override void Up()
    {
        Alter.Table("creator_sources")
            .AddColumn("next_full_scan_start_index").AsInt32().Nullable();

        Execute.Sql(
            "ALTER TABLE creator_sources ADD CONSTRAINT ck_creator_sources_next_full_scan_start_index_positive " +
            "CHECK (next_full_scan_start_index IS NULL OR next_full_scan_start_index > 0);");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE creator_sources DROP CONSTRAINT IF EXISTS ck_creator_sources_next_full_scan_start_index_positive;");
        Delete.Column("next_full_scan_start_index").FromTable("creator_sources");
    }
}
