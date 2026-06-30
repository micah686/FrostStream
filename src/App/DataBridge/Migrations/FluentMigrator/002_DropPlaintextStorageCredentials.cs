using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(2, "Drop plaintext storage credential columns; secrets now live in OpenBAO")]
public sealed class M002_DropPlaintextStorageCredentials : Migration
{
    private const string SchemaName = "storage";

    public override void Up()
    {
        Delete.Column("password").FromTable("storage_keys_network").InSchema(SchemaName);
        Delete.Column("private_key").FromTable("storage_keys_network").InSchema(SchemaName);
        Delete.Column("public_key").FromTable("storage_keys_network").InSchema(SchemaName);

        Delete.Column("access_key_id").FromTable("storage_keys_object_s3_compatible").InSchema(SchemaName);
        Delete.Column("secret_key_id").FromTable("storage_keys_object_s3_compatible").InSchema(SchemaName);
        Delete.Column("session_token_secret_id").FromTable("storage_keys_object_s3_compatible").InSchema(SchemaName);

        Alter.Table("storage_keys_object_s3_compatible").InSchema(SchemaName)
            .AddColumn("has_session_token").AsBoolean().NotNullable().WithDefaultValue(false);

        Delete.Column("azure_account_key_secret_id").FromTable("storage_keys_object_azure_blob").InSchema(SchemaName);
        Delete.Column("azure_connection_string_secret_id").FromTable("storage_keys_object_azure_blob").InSchema(SchemaName);
        Delete.Column("azure_sas_url_secret_id").FromTable("storage_keys_object_azure_blob").InSchema(SchemaName);

        Delete.Column("gcp_credentials_json").FromTable("storage_keys_object_google_cloud_storage").InSchema(SchemaName);
        Delete.Column("gcp_credentials_json_is_base64_encoded").FromTable("storage_keys_object_google_cloud_storage").InSchema(SchemaName);
    }

    public override void Down()
    {
        Alter.Table("storage_keys_network").InSchema(SchemaName)
            .AddColumn("password").AsString(2048).Nullable()
            .AddColumn("private_key").AsString(8192).Nullable()
            .AddColumn("public_key").AsString(8192).Nullable();

        Delete.Column("has_session_token").FromTable("storage_keys_object_s3_compatible").InSchema(SchemaName);

        Alter.Table("storage_keys_object_s3_compatible").InSchema(SchemaName)
            .AddColumn("access_key_id").AsString(512).NotNullable().WithDefaultValue(string.Empty)
            .AddColumn("secret_key_id").AsString(2048).NotNullable().WithDefaultValue(string.Empty)
            .AddColumn("session_token_secret_id").AsString(2048).Nullable();

        Alter.Table("storage_keys_object_azure_blob").InSchema(SchemaName)
            .AddColumn("azure_account_key_secret_id").AsString(2048).Nullable()
            .AddColumn("azure_connection_string_secret_id").AsString(4096).Nullable()
            .AddColumn("azure_sas_url_secret_id").AsString(4096).Nullable();

        Alter.Table("storage_keys_object_google_cloud_storage").InSchema(SchemaName)
            .AddColumn("gcp_credentials_json").AsCustom("jsonb").Nullable()
            .AddColumn("gcp_credentials_json_is_base64_encoded").AsBoolean().NotNullable().WithDefaultValue(false);
    }
}
