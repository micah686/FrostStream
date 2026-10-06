using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(95, "Add direct NFS, SMB, and CIFS connection settings")]
public sealed class M095_AddDirectNetworkStorageSettings : Migration
{
    private const string SchemaName = "storage";
    private const string TableName = "storage_keys_network";

    public override void Up()
    {
        Alter.Table(TableName).InSchema(SchemaName)
            .AddColumn("share_name").AsString(255).Nullable()
            .AddColumn("domain").AsString(255).Nullable()
            .AddColumn("export_path").AsString(2048).Nullable()
            .AddColumn("nfs_user_id").AsInt32().Nullable()
            .AddColumn("nfs_group_id").AsInt32().Nullable();
    }

    public override void Down()
    {
        Delete.Column("share_name").FromTable(TableName).InSchema(SchemaName);
        Delete.Column("domain").FromTable(TableName).InSchema(SchemaName);
        Delete.Column("export_path").FromTable(TableName).InSchema(SchemaName);
        Delete.Column("nfs_user_id").FromTable(TableName).InSchema(SchemaName);
        Delete.Column("nfs_group_id").FromTable(TableName).InSchema(SchemaName);
    }
}
