using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(2, "Drop plaintext storage credential columns; secrets now live in OpenBAO")]
public sealed class M002_DropPlaintextStorageCredentials : Migration
{
    public override void Up()
    {
        Delete.Column("password").FromTable("storage_keys_network");
        Delete.Column("private_key").FromTable("storage_keys_network");
        Delete.Column("public_key").FromTable("storage_keys_network");

        Delete.Column("access_key_id").FromTable("storage_keys_object_s3_compatible");
        Delete.Column("secret_key_id").FromTable("storage_keys_object_s3_compatible");
        Delete.Column("session_token_secret_id").FromTable("storage_keys_object_s3_compatible");

        Alter.Table("storage_keys_object_s3_compatible")
            .AddColumn("has_session_token").AsBoolean().NotNullable().WithDefaultValue(false);

        Delete.Column("azure_account_key_secret_id").FromTable("storage_keys_object_azure_blob");
        Delete.Column("azure_connection_string_secret_id").FromTable("storage_keys_object_azure_blob");
        Delete.Column("azure_sas_url_secret_id").FromTable("storage_keys_object_azure_blob");

        Delete.Column("gcp_credentials_json").FromTable("storage_keys_object_google_cloud_storage");
        Delete.Column("gcp_credentials_json_is_base64_encoded").FromTable("storage_keys_object_google_cloud_storage");
    }

    public override void Down()
    {
        Alter.Table("storage_keys_network")
            .AddColumn("password").AsString(2048).Nullable()
            .AddColumn("private_key").AsString(8192).Nullable()
            .AddColumn("public_key").AsString(8192).Nullable();

        Delete.Column("has_session_token").FromTable("storage_keys_object_s3_compatible");

        Alter.Table("storage_keys_object_s3_compatible")
            .AddColumn("access_key_id").AsString(512).NotNullable().WithDefaultValue(string.Empty)
            .AddColumn("secret_key_id").AsString(2048).NotNullable().WithDefaultValue(string.Empty)
            .AddColumn("session_token_secret_id").AsString(2048).Nullable();

        Alter.Table("storage_keys_object_azure_blob")
            .AddColumn("azure_account_key_secret_id").AsString(2048).Nullable()
            .AddColumn("azure_connection_string_secret_id").AsString(4096).Nullable()
            .AddColumn("azure_sas_url_secret_id").AsString(4096).Nullable();

        Alter.Table("storage_keys_object_google_cloud_storage")
            .AddColumn("gcp_credentials_json").AsCustom("jsonb").Nullable()
            .AddColumn("gcp_credentials_json_is_base64_encoded").AsBoolean().NotNullable().WithDefaultValue(false);
    }
}
