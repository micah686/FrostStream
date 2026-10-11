using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(55, "Store caption files by location; keep searchable text only in Typesense")]
public sealed class M055_RemoveCaptionTextContent : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE metadata.media_captions DROP COLUMN IF EXISTS text_content;");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE metadata.media_captions ADD COLUMN IF NOT EXISTS text_content TEXT NULL;");
    }
}
