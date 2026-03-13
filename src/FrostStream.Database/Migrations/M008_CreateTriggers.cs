using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(8, "Create database functions and triggers")]
public class M008_CreateTriggers : Migration
{
    public override void Up()
    {
        // Function to update search vector
        Execute.Sql(@"
            CREATE OR REPLACE FUNCTION update_video_search_vector()
            RETURNS TRIGGER AS $$
            BEGIN
                NEW.search_vector := 
                    setweight(to_tsvector('english', COALESCE(NEW.title, '')), 'A') ||
                    setweight(to_tsvector('english', COALESCE(NEW.description, '')), 'B');
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;");

        // Trigger to auto-update search vector
        Execute.Sql(@"
            CREATE TRIGGER trigger_update_video_search_vector
                BEFORE INSERT OR UPDATE ON videos
                FOR EACH ROW
                EXECUTE FUNCTION update_video_search_vector();");

        // Function to update updated_at timestamp
        Execute.Sql(@"
            CREATE OR REPLACE FUNCTION update_updated_at_column()
            RETURNS TRIGGER AS $$
            BEGIN
                NEW.updated_at = NOW();
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;");

        // Apply updated_at triggers to all relevant tables
        Execute.Sql("CREATE TRIGGER trigger_platforms_updated_at BEFORE UPDATE ON platforms " +
            "FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();");
        Execute.Sql("CREATE TRIGGER trigger_channels_updated_at BEFORE UPDATE ON channels " +
            "FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();");
        Execute.Sql("CREATE TRIGGER trigger_videos_updated_at BEFORE UPDATE ON videos " +
            "FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();");
        Execute.Sql("CREATE TRIGGER trigger_formats_updated_at BEFORE UPDATE ON video_formats " +
            "FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();");
        Execute.Sql("CREATE TRIGGER trigger_metadata_updated_at BEFORE UPDATE ON video_metadata " +
            "FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();");
        Execute.Sql("CREATE TRIGGER trigger_jobs_updated_at BEFORE UPDATE ON download_jobs " +
            "FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();");
        Execute.Sql("CREATE TRIGGER trigger_files_updated_at BEFORE UPDATE ON video_files " +
            "FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();");
    }

    public override void Down()
    {
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_platforms_updated_at ON platforms;");
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_channels_updated_at ON channels;");
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_videos_updated_at ON videos;");
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_formats_updated_at ON video_formats;");
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_metadata_updated_at ON video_metadata;");
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_jobs_updated_at ON download_jobs;");
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_files_updated_at ON video_files;");
        Execute.Sql("DROP TRIGGER IF EXISTS trigger_update_video_search_vector ON videos;");
        Execute.Sql("DROP FUNCTION IF EXISTS update_updated_at_column();");
        Execute.Sql("DROP FUNCTION IF EXISTS update_video_search_vector();");
    }
}
