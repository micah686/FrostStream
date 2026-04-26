using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(1, "Create normalized storage_keys schema and seed default storage config")]
public sealed class M001_CreateStorageKeysTable : Migration
{
    public override void Up()
    {
        Create.Table("storage_keys")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("key").AsString(100).NotNullable()
            .WithColumn("method").AsString(50).NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Create.UniqueConstraint("uq_storage_keys_key")
            .OnTable("storage_keys")
            .Column("key");

        Execute.Sql("ALTER TABLE storage_keys ADD CONSTRAINT ck_storage_keys_key_format CHECK (\"key\" ~ '^[a-z0-9-]{2,100}$');");

        Create.Table("storage_keys_local")
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("protocol").AsInt32().NotNullable()
            .WithColumn("path").AsString(2048).NotNullable();

        Create.Table("storage_keys_network")
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("protocol").AsInt32().NotNullable()
            .WithColumn("host").AsString(255).NotNullable()
            .WithColumn("port").AsInt32().Nullable()
            .WithColumn("username").AsString(255).Nullable()
            .WithColumn("password").AsString(2048).Nullable()
            .WithColumn("private_key").AsString(8192).Nullable()
            .WithColumn("public_key").AsString(8192).Nullable()
            .WithColumn("base_path").AsString(2048).Nullable();

        Create.Table("storage_keys_object")
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("provider").AsInt32().NotNullable()
            .WithColumn("container").AsString(255).NotNullable()
            .WithColumn("region").AsString(255).Nullable()
            .WithColumn("endpoint").AsString(2048).Nullable()
            .WithColumn("base_path").AsString(2048).Nullable()
            .WithColumn("access_key_id").AsString(512).Nullable()
            .WithColumn("secret_key").AsString(2048).Nullable()
            .WithColumn("use_default_credentials").AsBoolean().NotNullable().WithDefaultValue(true);

        Create.ForeignKey("fk_storage_keys_local_storage_key_id")
            .FromTable("storage_keys_local").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_network_storage_key_id")
            .FromTable("storage_keys_network").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_object_storage_key_id")
            .FromTable("storage_keys_object").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Insert.IntoTable("storage_keys")
            .Row(new
            {
                key = "default",
                method = "PosixLocal",
                description = "Fallback/Default Local Storage"
            });

        Execute.Sql(
            """
            INSERT INTO storage_keys_local (storage_key_id, protocol, path)
            SELECT id, 0, './data/'
            FROM storage_keys
            WHERE key = 'default';
            """);
    }

    public override void Down()
    {
        Delete.Table("storage_keys_object");
        Delete.Table("storage_keys_network");
        Delete.Table("storage_keys_local");
        Delete.Table("storage_keys");
    }
}
