using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(51, "Retick channel-update-check sweep to every 30 minutes")]
public sealed class M051_RetickChannelUpdateCheckCron : Migration
{
    public override void Up()
    {
        // With per-source update_check_interval_hours (migration 050) gating which sources are due,
        // the global sweep becomes a cheap tick: fire every 30 minutes and let DataBridge's due-filter
        // decide what to scan. Only retick if the cron is still the migration-049 seed value so a
        // user-edited cadence is respected.
        Execute.Sql("""
            UPDATE scheduling.scheduled_tasks
            SET cron = '0 0/30 * * * ?',
                last_updated = now()
            WHERE "key" = 'channel-update-check'
              AND cron = '0 0 */6 * * ?';
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            UPDATE scheduling.scheduled_tasks
            SET cron = '0 0 */6 * * ?',
                last_updated = now()
            WHERE "key" = 'channel-update-check'
              AND cron = '0 0/30 * * * ?';
            """);
    }
}
