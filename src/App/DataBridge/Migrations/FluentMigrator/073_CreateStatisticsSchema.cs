using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Download job rows (downloads.download_jobs) are periodically cleaned up, so anything derived
/// from them today — lifetime counts, historical activity, per-channel/per-media downloaded bytes —
/// silently degrades once the underlying job row is gone. These two tables capture just the
/// statistics-relevant facts at the moment a job is created or reaches a terminal state, so
/// dashboards stay correct independent of job retention.
/// </summary>
[Migration(73, "Create durable statistics tables decoupled from download_jobs retention")]
public sealed class M073_CreateStatisticsSchema : Migration
{
    private const string SchemaName = "statistics";

    public override void Up()
    {
        Create.Schema(SchemaName);

        // Global, tiny, forever: one row per (day, state) capturing how many jobs were created or
        // reached a given terminal state that day, plus completed bytes/duration for that day.
        Create.Table("download_daily_activity").InSchema(SchemaName)
            .WithColumn("day").AsDate().NotNullable()
            .WithColumn("state").AsString(32).NotNullable()
            .WithColumn("job_count").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("duration_seconds").AsDouble().NotNullable().WithDefaultValue(0);

        Create.PrimaryKey("pk_download_daily_activity")
            .OnTable("download_daily_activity").WithSchema(SchemaName)
            .Columns("day", "state");

        // Append-only: one row per completed download. Deliberately narrow — no urls, retry state,
        // or error text like download_jobs carries — just enough to roll up per-channel growth over
        // time and to know the last-known size/duration of a given media item.
        Create.Table("channel_media_downloads").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("account_id").AsInt64().NotNullable()
            .WithColumn("platform").AsString(50).NotNullable()
            .WithColumn("bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("duration_seconds").AsDouble().NotNullable().WithDefaultValue(0)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").NotNullable();

        Create.ForeignKey("fk_channel_media_downloads_account_id")
            .FromTable("channel_media_downloads").InSchema(SchemaName).ForeignColumn("account_id")
            .ToTable("accounts").InSchema("metadata").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_channel_media_downloads_account_completed_at")
            .OnTable("channel_media_downloads").InSchema(SchemaName)
            .OnColumn("account_id").Ascending()
            .OnColumn("completed_at").Ascending();

        Create.Index("ix_channel_media_downloads_media_guid_completed_at")
            .OnTable("channel_media_downloads").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .OnColumn("completed_at").Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_channel_media_downloads_media_guid_completed_at")
            .OnTable("channel_media_downloads").InSchema(SchemaName);
        Delete.Index("ix_channel_media_downloads_account_completed_at")
            .OnTable("channel_media_downloads").InSchema(SchemaName);
        Delete.ForeignKey("fk_channel_media_downloads_account_id")
            .OnTable("channel_media_downloads").InSchema(SchemaName);
        Delete.Table("channel_media_downloads").InSchema(SchemaName);

        Delete.Table("download_daily_activity").InSchema(SchemaName);

        Delete.Schema(SchemaName);
    }
}
