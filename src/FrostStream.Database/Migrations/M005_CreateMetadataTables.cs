using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(5, "Create metadata and heatmap tables")]
public class M005_CreateMetadataTables : Migration
{
    public override void Up()
    {
        // Video metadata table (flexible JSONB storage)
        Create.Table("video_metadata")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("category").AsString(50).NotNullable()
            .WithColumn("data").AsCustom("JSONB").NotNullable()
            .WithColumn("schema_version").AsInt32().WithDefaultValue(1)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Create.UniqueConstraint("idx_metadata_video_category")
            .OnTable("video_metadata")
            .Columns("video_id", "category");

        Execute.Sql("CREATE INDEX idx_metadata_video ON video_metadata(video_id);");
        Execute.Sql("CREATE INDEX idx_metadata_category ON video_metadata(category);");
        Execute.Sql("CREATE INDEX idx_metadata_data ON video_metadata USING gin(data);");

        // Heatmaps table (YouTube-style engagement data)
        Create.Table("video_heatmaps")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("start_time").AsDecimal(10, 3).NotNullable()
            .WithColumn("end_time").AsDecimal(10, 3).NotNullable()
            .WithColumn("value").AsDecimal(5, 4).NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Execute.Sql("CREATE INDEX idx_heatmaps_video ON video_heatmaps(video_id);");
        Execute.Sql("CREATE INDEX idx_heatmaps_time ON video_heatmaps(video_id, start_time, end_time);");
    }

    public override void Down()
    {
        Delete.Table("video_heatmaps");
        Delete.Table("video_metadata");
    }
}
