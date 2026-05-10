using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(1, "Create normalized storage_keys schema and seed default storage config")]
public sealed class M001_CreateStorageKeysTable : Migration
{
    public override void Up()
    {
        Execute.Sql("CREATE TYPE local_storage_protocol AS ENUM ('local');");
        Execute.Sql("CREATE TYPE network_storage_protocol AS ENUM ('ftp', 'ftps', 'sftp', 'nfs', 'smb', 'cifs');");
        Execute.Sql("CREATE TYPE s3_compatible_object_storage_provider AS ENUM ('aws_s3', 'min_io', 'digital_ocean_spaces');");
        Execute.Sql("CREATE TYPE azure_blob_credential_mode AS ENUM ('account_key', 'connection_string', 'sas_url');");
        Execute.Sql("CREATE TYPE google_cloud_storage_credential_mode AS ENUM ('credentials_json', 'credentials_file_path', 'workload_identity', 'default_credentials');");

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
            .WithColumn("protocol").AsCustom("local_storage_protocol").NotNullable()
            .WithColumn("path").AsString(2048).NotNullable();

        Create.Table("storage_keys_network")
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("protocol").AsCustom("network_storage_protocol").NotNullable()
            .WithColumn("host").AsString(255).NotNullable()
            .WithColumn("port").AsInt32().Nullable()
            .WithColumn("username").AsString(255).Nullable()
            .WithColumn("password").AsString(2048).Nullable()
            .WithColumn("private_key").AsString(8192).Nullable()
            .WithColumn("public_key").AsString(8192).Nullable()
            .WithColumn("base_path").AsString(2048).Nullable();

        Create.Table("storage_keys_object_s3_compatible")
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("provider").AsCustom("s3_compatible_object_storage_provider").NotNullable()
            .WithColumn("bucket_name").AsString(255).NotNullable()
            .WithColumn("region").AsString(255).Nullable()
            .WithColumn("endpoint").AsString(2048).Nullable()
            .WithColumn("access_key_id").AsString(512).NotNullable()
            .WithColumn("secret_key_id").AsString(2048).NotNullable()
            .WithColumn("session_token_secret_id").AsString(2048).Nullable()
            .WithColumn("force_path_style").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("use_ssl").AsBoolean().Nullable();

        Create.Table("storage_keys_object_azure_blob")
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("credential_mode").AsCustom("azure_blob_credential_mode").NotNullable()
            .WithColumn("container_name").AsString(255).Nullable()
            .WithColumn("azure_account_name").AsString(255).Nullable()
            .WithColumn("azure_account_key_secret_id").AsString(2048).Nullable()
            .WithColumn("azure_connection_string_secret_id").AsString(4096).Nullable()
            .WithColumn("azure_sas_url_secret_id").AsString(4096).Nullable();

        Create.Table("storage_keys_object_google_cloud_storage")
            .WithColumn("storage_key_id").AsInt32().PrimaryKey()
            .WithColumn("bucket_name").AsString(255).NotNullable()
            .WithColumn("credential_mode").AsCustom("google_cloud_storage_credential_mode").NotNullable()
            .WithColumn("gcp_credentials_json").AsCustom("jsonb").Nullable()
            .WithColumn("gcp_credentials_json_is_base64_encoded").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("gcp_credentials_file_path").AsString(2048).Nullable()
            .WithColumn("gcp_project_id").AsString(255).Nullable();

        Create.ForeignKey("fk_storage_keys_local_storage_key_id")
            .FromTable("storage_keys_local").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_network_storage_key_id")
            .FromTable("storage_keys_network").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_object_s3_compatible_storage_key_id")
            .FromTable("storage_keys_object_s3_compatible").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_object_azure_blob_storage_key_id")
            .FromTable("storage_keys_object_azure_blob").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_storage_keys_object_google_cloud_storage_storage_key_id")
            .FromTable("storage_keys_object_google_cloud_storage").ForeignColumn("storage_key_id")
            .ToTable("storage_keys").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Insert.IntoTable("storage_keys")
            .Row(new
            {
                key = "default",
                method = "Local",
                description = "Fallback/Default Local Storage"
            });

        Execute.Sql(
            """
            INSERT INTO storage_keys_local (storage_key_id, protocol, path)
            SELECT id, 'local'::local_storage_protocol, './data/'
            FROM storage_keys
            WHERE key = 'default';
            """);
    }

    public override void Down()
    {
        Delete.Table("storage_keys_object_google_cloud_storage");
        Delete.Table("storage_keys_object_azure_blob");
        Delete.Table("storage_keys_object_s3_compatible");
        Delete.Table("storage_keys_network");
        Delete.Table("storage_keys_local");
        Delete.Table("storage_keys");
        Execute.Sql("DROP TYPE google_cloud_storage_credential_mode;");
        Execute.Sql("DROP TYPE azure_blob_credential_mode;");
        Execute.Sql("DROP TYPE s3_compatible_object_storage_provider;");
        Execute.Sql("DROP TYPE network_storage_protocol;");
        Execute.Sql("DROP TYPE local_storage_protocol;");
    }
}
