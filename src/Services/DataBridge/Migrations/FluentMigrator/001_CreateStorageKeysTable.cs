using FluentMigrator;
using Shared;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(1, "Create storage_keys table and seed default storage config")]
public sealed class M001_CreateStorageKeysTable : Migration
{
    public override void Up()
    {
        Create.Table("storage_keys")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("key").AsString(100).NotNullable()
            .WithColumn("method").AsInt32().NotNullable()
            .WithColumn("parameters").AsCustom("jsonb").NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Create.UniqueConstraint("uq_storage_keys_key")
            .OnTable("storage_keys")
            .Column("key");

        Execute.Sql("ALTER TABLE storage_keys ADD CONSTRAINT ck_storage_keys_key_format CHECK (\"key\" ~ '^[a-z0-9-]{2,100}$');");

        Insert.IntoTable("storage_keys")
            .Row(new
            {
                key = "default",
                method = (int)StorageMethod.PosixLocal,
                parameters = "{\"path\":\"./data/\"}",
                description = "Fallback/Default Local Storage"
            });
    }

    public override void Down()
    {
        Delete.Table("storage_keys");
    }
}
