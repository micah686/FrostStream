using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(9, "Create materialized views for statistics")]
public class M009_CreateMaterializedViews : Migration
{
    public override void Up()
    {
        // Channel statistics materialized view
        Execute.Sql(@"
            CREATE MATERIALIZED VIEW channel_stats AS
            SELECT 
                c.id AS channel_id,
                c.name AS channel_name,
                p.name AS platform_name,
                COUNT(v.id) AS video_count,
                SUM(v.duration) AS total_duration,
                SUM(v.view_count) AS total_views,
                MAX(v.uploaded_at) AS last_upload,
                COUNT(CASE WHEN v.is_live THEN 1 END) AS live_count
            FROM channels c
            JOIN platforms p ON c.platform_id = p.id
            LEFT JOIN videos v ON c.id = v.channel_id AND NOT v.is_deleted
            GROUP BY c.id, c.name, p.name;");

        Execute.Sql("CREATE UNIQUE INDEX idx_channel_stats_channel ON channel_stats(channel_id);");

        // Platform summary materialized view
        Execute.Sql(@"
            CREATE MATERIALIZED VIEW platform_summary AS
            SELECT 
                p.id AS platform_id,
                p.name AS platform_name,
                COUNT(DISTINCT c.id) AS channel_count,
                COUNT(v.id) AS video_count,
                SUM(v.duration) AS total_duration_seconds,
                SUM(vf.size_bytes) AS total_storage_bytes
            FROM platforms p
            LEFT JOIN channels c ON p.id = c.platform_id
            LEFT JOIN videos v ON p.id = v.platform_id AND NOT v.is_deleted
            LEFT JOIN video_files vf ON v.id = vf.video_id AND vf.status = 'active'
            GROUP BY p.id, p.name;");

        Execute.Sql("CREATE UNIQUE INDEX idx_platform_summary_platform ON platform_summary(platform_id);");

        // Function to refresh materialized views
        Execute.Sql(@"
            CREATE OR REPLACE FUNCTION refresh_materialized_views()
            RETURNS void AS $$
            BEGIN
                REFRESH MATERIALIZED VIEW CONCURRENTLY channel_stats;
                REFRESH MATERIALIZED VIEW CONCURRENTLY platform_summary;
            END;
            $$ LANGUAGE plpgsql;");
    }

    public override void Down()
    {
        Execute.Sql("DROP FUNCTION IF EXISTS refresh_materialized_views();");
        Execute.Sql("DROP MATERIALIZED VIEW IF EXISTS platform_summary;");
        Execute.Sql("DROP MATERIALIZED VIEW IF EXISTS channel_stats;");
    }
}
