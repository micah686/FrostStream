using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Splits the per-source background-job bookkeeping out of discovery.creator_sources into a 1:1
/// jobs.creator_scan_state table, mirroring what migration 076 did for jobs.playlists. Two jobs write
/// this state: the channel discovery scans (the four scan cursors) and the channel asset refresh
/// (avatar/banner URLs + content hashes, which are "what the last refresh fetched" bookkeeping — the
/// durable blob paths have lived in metadata.accounts since migration 024 — plus its retry counters).
/// What remains on creator_sources is creator identity and the user-owned scan policy.
/// </summary>
[Migration(77, "Split creator_sources scan/asset job state into jobs.creator_scan_state")]
public sealed class M077_SplitCreatorSourceScanState : Migration
{
    private const string JobsSchema = "jobs";
    private const string DiscoverySchema = "discovery";

    private static readonly string[] MovedColumns =
    [
        "last_successful_scan_at",
        "last_full_scan_at",
        "last_seen_high_watermark",
        "next_full_scan_start_index",
        "avatar_url",
        "avatar_content_hash",
        "banner_url",
        "banner_content_hash",
        "assets_last_refreshed_at",
        "assets_last_attempt_at",
        "assets_attempt_count",
        "assets_last_error",
    ];

    public override void Up()
    {
        Create.Table("creator_scan_state").InSchema(JobsSchema)
            .WithColumn("creator_source_id").AsInt64().PrimaryKey()
            .WithColumn("last_successful_scan_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_full_scan_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_seen_high_watermark").AsString(512).Nullable()
            .WithColumn("next_full_scan_start_index").AsInt32().Nullable()
            .WithColumn("avatar_url").AsString(4096).Nullable()
            .WithColumn("avatar_content_hash").AsString(64).Nullable()
            .WithColumn("banner_url").AsString(4096).Nullable()
            .WithColumn("banner_content_hash").AsString(64).Nullable()
            .WithColumn("assets_last_refreshed_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("assets_last_attempt_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("assets_attempt_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("assets_last_error").AsString(2048).Nullable();

        Create.ForeignKey("fk_creator_scan_state_creator_source_id")
            .FromTable("creator_scan_state").InSchema(JobsSchema).ForeignColumn("creator_source_id")
            .ToTable("creator_sources").InSchema(DiscoverySchema).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        // The cursor's CHECK moves with the column (originally added in migration 022).
        Execute.Sql(
            "ALTER TABLE jobs.creator_scan_state ADD CONSTRAINT ck_creator_scan_state_next_full_scan_start_index_positive " +
            "CHECK (next_full_scan_start_index IS NULL OR next_full_scan_start_index > 0);");

        // Every existing source gets its companion row so the 1:1 invariant holds from the start.
        Execute.Sql(
            $"INSERT INTO jobs.creator_scan_state (creator_source_id, {string.Join(", ", MovedColumns)}) " +
            $"SELECT id, {string.Join(", ", MovedColumns)} FROM discovery.creator_sources;");

        Execute.Sql(
            "ALTER TABLE discovery.creator_sources " +
            "DROP CONSTRAINT IF EXISTS ck_creator_sources_next_full_scan_start_index_positive;");

        foreach (var column in MovedColumns)
        {
            Delete.Column(column).FromTable("creator_sources").InSchema(DiscoverySchema);
        }
    }

    public override void Down()
    {
        Alter.Table("creator_sources").InSchema(DiscoverySchema)
            .AddColumn("last_successful_scan_at").AsCustom("timestamp with time zone").Nullable()
            .AddColumn("last_full_scan_at").AsCustom("timestamp with time zone").Nullable()
            .AddColumn("last_seen_high_watermark").AsString(512).Nullable()
            .AddColumn("next_full_scan_start_index").AsInt32().Nullable()
            .AddColumn("avatar_url").AsString(4096).Nullable()
            .AddColumn("avatar_content_hash").AsString(64).Nullable()
            .AddColumn("banner_url").AsString(4096).Nullable()
            .AddColumn("banner_content_hash").AsString(64).Nullable()
            .AddColumn("assets_last_refreshed_at").AsCustom("timestamp with time zone").Nullable()
            .AddColumn("assets_last_attempt_at").AsCustom("timestamp with time zone").Nullable()
            .AddColumn("assets_attempt_count").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("assets_last_error").AsString(2048).Nullable();

        Execute.Sql(
            "UPDATE discovery.creator_sources cs SET " +
            string.Join(", ", MovedColumns.Select(c => $"{c} = s.{c}")) + " " +
            "FROM jobs.creator_scan_state s WHERE s.creator_source_id = cs.id;");

        Execute.Sql(
            "ALTER TABLE discovery.creator_sources ADD CONSTRAINT ck_creator_sources_next_full_scan_start_index_positive " +
            "CHECK (next_full_scan_start_index IS NULL OR next_full_scan_start_index > 0);");

        Delete.ForeignKey("fk_creator_scan_state_creator_source_id")
            .OnTable("creator_scan_state").InSchema(JobsSchema);
        Delete.Table("creator_scan_state").InSchema(JobsSchema);
    }
}
