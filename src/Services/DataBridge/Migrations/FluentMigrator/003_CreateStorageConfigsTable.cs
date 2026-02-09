using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(3)]
public class CreateStorageConfigsTable : Migration
{
    public override void Up()
    {
        Create.Table("storage_configs")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("key").AsString(100).NotNullable().Unique()
            .WithColumn("method").AsString(50).NotNullable()
            .WithColumn("connection_string").AsString(2000).NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("remote_path").AsString(1000).Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().Nullable();

        // Create index on key for fast lookups
        Create.Index("IX_storage_configs_key")
            .OnTable("storage_configs")
            .OnColumn("key");
    }

    public override void Down()
    {
        Delete.Table("storage_configs");
    }
}
