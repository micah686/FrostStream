using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(1, "Create normalized storage_keys schema and seed default storage config")]
public sealed class M001_CreateStorageKeysTable : Migration
{
    private const string SchemaName = "storage";

    public override void Up()
    {
        Create.Schema(SchemaName);

        Execute.Sql("CREATE TYPE storage.local_storage_protocol AS ENUM ('local');");
        Execute.Sql("CREATE TYPE storage.network_storage_protocol AS ENUM ('ftp', 'ftps', 'sftp', 'nfs', 'smb', 'cifs');");
        Execute.Sql("CREATE TYPE storage.s3_compatible_object_storage_provider AS ENUM ('aws_s3', 'min_io', 'digital_ocean_spaces');");
        Execute.Sql("CREATE TYPE storage.azure_blob_credential_mode AS ENUM ('account_key', 'connection_string', 'sas_url');");
        Execute.Sql("CREATE TYPE storage.google_cloud_storage_credential_mode AS ENUM ('credentials_json', 'credentials_file_path', 'workload_identity', 'default_credentials');");

        Create.Table("storage_keys").InSchema(SchemaName)
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("key").AsString(100).NotNullable()
            .WithColumn("method").AsString(50).NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Create.UniqueConstraint("uq_storage_keys_key")
            .OnTable("storage_keys").WithSchema(SchemaName)
            .Column("key");

        Execute.Sql("ALTER TABLE storage.storage_keys ADD CONSTRAINT ck_storage_keys_key_format CHECK (\"key\" ~ '^[a-z0-9-]{2,100}$');");

        Create.Table("storage_keys_local").InSchema(SchemaName)
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("protocol").AsCustom("storage.local_storage_protocol").NotNullable()
            .WithColumn("path").AsString(2048).NotNullable();

        Create.Table("storage_keys_network").InSchema(SchemaName)
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("protocol").AsCustom("storage.network_storage_protocol").NotNullable()
            .WithColumn("host").AsString(255).NotNullable()
            .WithColumn("port").AsInt32().Nullable()
            .WithColumn("username").AsString(255).Nullable()
            .WithColumn("password").AsString(2048).Nullable()
            .WithColumn("private_key").AsString(8192).Nullable()
            .WithColumn("public_key").AsString(8192).Nullable()
            .WithColumn("base_path").AsString(2048).Nullable();

        Create.Table("storage_keys_object_s3_compatible").InSchema(SchemaName)
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("provider").AsCustom("storage.s3_compatible_object_storage_provider").NotNullable()
            .WithColumn("bucket_name").AsString(255).NotNullable()
            .WithColumn("region").AsString(255).Nullable()
            .WithColumn("endpoint").AsString(2048).Nullable()
            .WithColumn("access_key_id").AsString(512).NotNullable()
            .WithColumn("secret_key_id").AsString(2048).NotNullable()
            .WithColumn("session_token_secret_id").AsString(2048).Nullable()
            .WithColumn("force_path_style").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("use_ssl").AsBoolean().Nullable();

        Create.Table("storage_keys_object_azure_blob").InSchema(SchemaName)
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("credential_mode").AsCustom("storage.azure_blob_credential_mode").NotNullable()
            .WithColumn("container_name").AsString(255).Nullable()
            .WithColumn("azure_account_name").AsString(255).Nullable()
            .WithColumn("azure_account_key_secret_id").AsString(2048).Nullable()
            .WithColumn("azure_connection_string_secret_id").AsString(4096).Nullable()
            .WithColumn("azure_sas_url_secret_id").AsString(4096).Nullable();

        Create.Table("storage_keys_object_google_cloud_storage").InSchema(SchemaName)
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("bucket_name").AsString(255).NotNullable()
            .WithColumn("credential_mode").AsCustom("storage.google_cloud_storage_credential_mode").NotNullable()
            .WithColumn("gcp_credentials_json").AsCustom("jsonb").Nullable()
            .WithColumn("gcp_credentials_json_is_base64_encoded").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("gcp_credentials_file_path").AsString(2048).Nullable()
            .WithColumn("gcp_project_id").AsString(255).Nullable();

        Create.ForeignKey("fk_storage_keys_local_storage_key_id")
            .FromTable("storage_keys_local").InSchema(SchemaName).ForeignColumn("storage_key_id")
            .ToTable("storage_keys").InSchema(SchemaName).PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_network_storage_key_id")
            .FromTable("storage_keys_network").InSchema(SchemaName).ForeignColumn("storage_key_id")
            .ToTable("storage_keys").InSchema(SchemaName).PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_object_s3_compatible_storage_key_id")
            .FromTable("storage_keys_object_s3_compatible").InSchema(SchemaName).ForeignColumn("storage_key_id")
            .ToTable("storage_keys").InSchema(SchemaName).PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_object_azure_blob_storage_key_id")
            .FromTable("storage_keys_object_azure_blob").InSchema(SchemaName).ForeignColumn("storage_key_id")
            .ToTable("storage_keys").InSchema(SchemaName).PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_object_google_cloud_storage_storage_key_id")
            .FromTable("storage_keys_object_google_cloud_storage").InSchema(SchemaName).ForeignColumn("storage_key_id")
            .ToTable("storage_keys").InSchema(SchemaName).PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Insert.IntoTable("storage_keys").InSchema(SchemaName)
            .Row(new
            {
                key = "default",
                method = "Local",
                description = "Fallback/Default Local Storage"
            });

        Execute.Sql(
            """
            INSERT INTO storage.storage_keys_local (storage_key_id, protocol, path)
            SELECT id, 'local'::storage.local_storage_protocol, './data/'
            FROM storage.storage_keys
            WHERE key = 'default';
            """);
    }

    public override void Down()
    {
        Delete.Table("storage_keys_object_google_cloud_storage").InSchema(SchemaName);
        Delete.Table("storage_keys_object_azure_blob").InSchema(SchemaName);
        Delete.Table("storage_keys_object_s3_compatible").InSchema(SchemaName);
        Delete.Table("storage_keys_network").InSchema(SchemaName);
        Delete.Table("storage_keys_local").InSchema(SchemaName);
        Delete.Table("storage_keys").InSchema(SchemaName);
        Execute.Sql("DROP TYPE storage.google_cloud_storage_credential_mode;");
        Execute.Sql("DROP TYPE storage.azure_blob_credential_mode;");
        Execute.Sql("DROP TYPE storage.s3_compatible_object_storage_provider;");
        Execute.Sql("DROP TYPE storage.network_storage_protocol;");
        Execute.Sql("DROP TYPE storage.local_storage_protocol;");
        Delete.Schema(SchemaName);
    }
}
