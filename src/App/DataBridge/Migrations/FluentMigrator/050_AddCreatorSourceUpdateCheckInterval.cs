using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(50, "Add per-source incremental update-check interval to creator sources")]
public sealed class M050_AddCreatorSourceUpdateCheckInterval : Migration
{
    public override void Up()
    {
        // Minimum hours between incremental update-check scans of a source. The global
        // channel-update-check schedule becomes a frequent tick (see migration 051); each firing
        // only scans sources whose last_successful_scan_at + this interval is due — mirroring how
        // full_rescan_interval_days already gates the daily-channel-full-rescan sweep.
        Alter.Table("creator_sources").InSchema("discovery")
            .AddColumn("update_check_interval_hours").AsInt32().NotNullable().WithDefaultValue(6);
    }

    public override void Down()
    {
        Delete.Column("update_check_interval_hours").FromTable("creator_sources").InSchema("discovery");
    }
}
