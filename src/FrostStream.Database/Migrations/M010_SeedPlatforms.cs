using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(10, "Seed default platforms")]
public class M010_SeedPlatforms : Migration
{
    public override void Up()
    {
        // Insert default platforms
        Insert.IntoTable("platforms")
            .Row(new 
            { 
                name = "youtube", 
                display_name = "YouTube", 
                domain = "youtube.com",
                supports_live = true,
                supports_captions = true,
                supports_heatmaps = true
            })
            .Row(new 
            { 
                name = "twitch", 
                display_name = "Twitch", 
                domain = "twitch.tv",
                supports_live = true,
                supports_captions = true,
                supports_heatmaps = false
            })
            .Row(new 
            { 
                name = "kick", 
                display_name = "Kick", 
                domain = "kick.com",
                supports_live = true,
                supports_captions = false,
                supports_heatmaps = false
            })
            .Row(new 
            { 
                name = "rumble", 
                display_name = "Rumble", 
                domain = "rumble.com",
                supports_live = false,
                supports_captions = false,
                supports_heatmaps = false
            })
            .Row(new 
            { 
                name = "vimeo", 
                display_name = "Vimeo", 
                domain = "vimeo.com",
                supports_live = false,
                supports_captions = true,
                supports_heatmaps = false
            });
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM platforms WHERE name IN ('youtube', 'twitch', 'kick', 'rumble', 'vimeo');");
    }
}
