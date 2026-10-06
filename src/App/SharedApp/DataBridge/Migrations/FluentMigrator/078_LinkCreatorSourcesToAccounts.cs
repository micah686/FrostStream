using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Gives discovery.creator_sources a real foreign key to metadata.accounts. Until now the two were
/// associated only by matching (platform, account_handle) strings at asset-refresh write time, with no
/// referential integrity and nothing persisted on the source row. The backfill derives each source's
/// account the same way the statistics queries already do — through discovered_media → media_metadata —
/// taking the account that owns the most of the source's discovered media.
/// ON DELETE SET NULL: removing an account must not delete the monitored source.
/// </summary>
[Migration(78, "Link discovery.creator_sources to metadata.accounts via account_id")]
public sealed class M078_LinkCreatorSourcesToAccounts : Migration
{
    private const string DiscoverySchema = "discovery";

    public override void Up()
    {
        Alter.Table("creator_sources").InSchema(DiscoverySchema)
            .AddColumn("account_id").AsInt64().Nullable();

        Create.ForeignKey("fk_creator_sources_account_id")
            .FromTable("creator_sources").InSchema(DiscoverySchema).ForeignColumn("account_id")
            .ToTable("accounts").InSchema("metadata").PrimaryColumn("id")
            .OnDelete(Rule.SetNull);

        Create.Index("ix_creator_sources_account_id")
            .OnTable("creator_sources").InSchema(DiscoverySchema)
            .OnColumn("account_id").Ascending();

        // Platform casing is inconsistent across the two tables ("YouTube" vs "youtube"), so the
        // backfill matches case-insensitively rather than mirroring the exact-match statistics join.
        Execute.Sql("""
            UPDATE discovery.creator_sources cs
            SET account_id = link.account_id
            FROM (
                SELECT
                    dm.creator_source_id,
                    mm.account_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY dm.creator_source_id
                        ORDER BY COUNT(*) DESC, mm.account_id
                    ) AS rn
                FROM discovery.discovered_media dm
                JOIN metadata.media_metadata mm ON mm.external_media_id = dm.external_media_id
                JOIN metadata.accounts a ON a.id = mm.account_id
                JOIN discovery.creator_sources s
                    ON s.id = dm.creator_source_id
                    AND lower(s.platform) = lower(a.platform)
                GROUP BY dm.creator_source_id, mm.account_id
            ) link
            WHERE link.creator_source_id = cs.id
              AND link.rn = 1;
            """);
    }

    public override void Down()
    {
        Delete.Index("ix_creator_sources_account_id").OnTable("creator_sources").InSchema(DiscoverySchema);
        Delete.ForeignKey("fk_creator_sources_account_id").OnTable("creator_sources").InSchema(DiscoverySchema);
        Delete.Column("account_id").FromTable("creator_sources").InSchema(DiscoverySchema);
    }
}
