using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(1)]
public class CreateStorageConfigsTable : Migration
{
    public override void Up()
    {
        Create.Table("storage_configs")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("key").AsString(100).NotNullable().Unique()
            .WithColumn("method").AsString(50).NotNullable()
            .WithColumn("parameters").AsCustom("jsonb").NotNullable()
            .WithColumn("description").AsString().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().Nullable();

        Insert.IntoTable("storage_configs").Row(new
        {
            key = "default",
            method = "PosixLocal",
            parameters = "{\"path\": \"/tmp/froststream\"}",
            description = "Default local storage",
            created_at = DateTime.UtcNow
        });
    }

    public override void Down()
    {
        Delete.Table("storage_configs");
    }
}
