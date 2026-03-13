using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(7, "Create audit history table and triggers")]
public class M007_CreateAuditTables : Migration
{
    public override void Up()
    {
        // Video history table for audit trail
        Create.Table("video_history")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("field_name").AsString(100).NotNullable()
            .WithColumn("old_value").AsString(int.MaxValue).Nullable()
            .WithColumn("new_value").AsString(int.MaxValue).Nullable()
            .WithColumn("changed_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("change_source").AsString(100).Nullable()
            .WithColumn("snapshot").AsCustom("JSONB").Nullable();

        Execute.Sql("CREATE INDEX idx_history_video ON video_history(video_id, changed_at DESC);");
        Execute.Sql("CREATE INDEX idx_history_field ON video_history(field_name);");
    }

    public override void Down()
    {
        Delete.Table("video_history");
    }
}
