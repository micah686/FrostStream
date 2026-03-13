using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(4, "Create tags and categories tables")]
public class M004_CreateTaxonomyTables : Migration
{
    public override void Up()
    {
        // Tags table
        Create.Table("tags")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("name").AsString(100).NotNullable().Unique()
            .WithColumn("slug").AsString(100).NotNullable().Unique()
            .WithColumn("description").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Execute.Sql("CREATE INDEX idx_tags_slug ON tags(slug);");

        // Video-Tags junction table
        Create.Table("video_tags")
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("tag_id").AsGuid().NotNullable().ForeignKey("tags", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Create.PrimaryKey("pk_video_tags").OnTable("video_tags").Columns("video_id", "tag_id");
        Execute.Sql("CREATE INDEX idx_video_tags_tag ON video_tags(tag_id);");

        // Categories table
        Create.Table("categories")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("platform_id").AsGuid().Nullable().ForeignKey("platforms", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("name").AsString(100).NotNullable()
            .WithColumn("slug").AsString(100).NotNullable()
            .WithColumn("description").AsString(int.MaxValue).Nullable()
            .WithColumn("external_id").AsString(100).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Create.UniqueConstraint("idx_categories_platform_external")
            .OnTable("categories")
            .Columns("platform_id", "external_id");

        // Video-Categories junction table
        Create.Table("video_categories")
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("category_id").AsGuid().NotNullable().ForeignKey("categories", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("is_primary").AsBoolean().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Create.PrimaryKey("pk_video_categories").OnTable("video_categories").Columns("video_id", "category_id");
        Execute.Sql("CREATE INDEX idx_video_categories_category ON video_categories(category_id);");
        Execute.Sql("CREATE INDEX idx_video_categories_primary ON video_categories(video_id, is_primary) WHERE is_primary = true;");

        // Insert default categories
        Insert.IntoTable("categories")
            .Row(new { name = "Uncategorized", slug = "uncategorized", description = "Videos without a specific category" });
    }

    public override void Down()
    {
        Delete.Table("video_categories");
        Delete.Table("categories");
        Delete.Table("video_tags");
        Delete.Table("tags");
    }
}
