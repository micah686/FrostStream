using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(66, "Add mount path for mount-backed NFS, SMB, and CIFS storage")]
public sealed class M066_AddNetworkStorageMountPath : Migration
{
    private const string SchemaName = "storage";
    private const string TableName = "storage_keys_network";

    public override void Up()
    {
        Alter.Table(TableName).InSchema(SchemaName)
            .AddColumn("mount_path").AsString(2048).Nullable();
    }

    public override void Down()
    {
        Delete.Column("mount_path").FromTable(TableName).InSchema(SchemaName);
    }
}
