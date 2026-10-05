using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// "Recent download states" on the admin stats channel-detail view was reading straight from
/// jobs.download_jobs, joined by URL — the same table DownloadHistoryPurger hard-deletes terminal
/// rows from after ~30 days (and nightly, via the seeded download_history_cleanup task). Once a
/// channel's jobs aged out, the card went permanently blank even though the downloads succeeded.
/// These two tables capture state transitions durably at the moment they happen, the same way
/// statistics.download_daily_activity already does for the global view.
/// </summary>
[Migration(94, "Create durable per-creator-source and per-account download state ledgers")]
public sealed class M094_CreateChannelDownloadStateLedgers : Migration
{
    private const string SchemaName = "statistics";

    public override void Up()
    {
        Create.Table("creator_source_daily_states").InSchema(SchemaName)
            .WithColumn("day").AsDate().NotNullable()
            .WithColumn("creator_source_id").AsInt64().NotNullable()
            .WithColumn("state").AsString(32).NotNullable()
            .WithColumn("job_count").AsInt64().NotNullable().WithDefaultValue(0);

        Create.PrimaryKey("pk_creator_source_daily_states")
            .OnTable("creator_source_daily_states").WithSchema(SchemaName)
            .Columns("day", "creator_source_id", "state");

        Create.ForeignKey("fk_creator_source_daily_states_creator_source_id")
            .FromTable("creator_source_daily_states").InSchema(SchemaName).ForeignColumn("creator_source_id")
            .ToTable("creator_sources").InSchema("discovery").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_creator_source_daily_states_creator_source_id")
            .OnTable("creator_source_daily_states").InSchema(SchemaName)
            .OnColumn("creator_source_id").Ascending();

        Create.Table("account_daily_states").InSchema(SchemaName)
            .WithColumn("day").AsDate().NotNullable()
            .WithColumn("account_id").AsInt64().NotNullable()
            .WithColumn("state").AsString(32).NotNullable()
            .WithColumn("job_count").AsInt64().NotNullable().WithDefaultValue(0);

        Create.PrimaryKey("pk_account_daily_states")
            .OnTable("account_daily_states").WithSchema(SchemaName)
            .Columns("day", "account_id", "state");

        Create.ForeignKey("fk_account_daily_states_account_id")
            .FromTable("account_daily_states").InSchema(SchemaName).ForeignColumn("account_id")
            .ToTable("accounts").InSchema("metadata").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_account_daily_states_account_id")
            .OnTable("account_daily_states").InSchema(SchemaName)
            .OnColumn("account_id").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_account_daily_states_account_id").OnTable("account_daily_states").InSchema(SchemaName);
        Delete.ForeignKey("fk_account_daily_states_account_id").OnTable("account_daily_states").InSchema(SchemaName);
        Delete.Table("account_daily_states").InSchema(SchemaName);

        Delete.Index("ix_creator_source_daily_states_creator_source_id").OnTable("creator_source_daily_states").InSchema(SchemaName);
        Delete.ForeignKey("fk_creator_source_daily_states_creator_source_id").OnTable("creator_source_daily_states").InSchema(SchemaName);
        Delete.Table("creator_source_daily_states").InSchema(SchemaName);
    }
}
