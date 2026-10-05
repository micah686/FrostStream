using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Supports the admin stats "coverage" summary, which now groups discovery_status (and platform)
/// across all of discovery.discovered_media in one shot instead of paging through every channel.
/// </summary>
[Migration(93, "Add platform/discovery_status index to discovered_media for coverage summary aggregates")]
public sealed class M093_AddDiscoveredMediaPlatformStatusIndex : Migration
{
    public override void Up()
    {
        Create.Index("ix_discovered_media_platform_discovery_status")
            .OnTable("discovered_media").InSchema("discovery")
            .OnColumn("platform").Ascending()
            .OnColumn("discovery_status").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_discovered_media_platform_discovery_status")
            .OnTable("discovered_media").InSchema("discovery");
    }
}
