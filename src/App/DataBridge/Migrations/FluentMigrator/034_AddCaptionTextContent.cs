using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(34, "Add text_content column to media_captions for full-text subtitle search")]
public sealed class M034_AddCaptionTextContent : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE metadata.media_captions
                ADD COLUMN IF NOT EXISTS text_content TEXT NULL;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            ALTER TABLE metadata.media_captions
                DROP COLUMN IF EXISTS text_content;
            """);
    }
}
