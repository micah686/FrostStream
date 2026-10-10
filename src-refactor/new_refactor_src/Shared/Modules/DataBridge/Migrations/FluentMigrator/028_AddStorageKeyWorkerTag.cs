using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(28, "Add worker_tag column to storage_keys")]
public sealed class M028_AddStorageKeyWorkerTag : Migration
{
    private const string SchemaName = "storage";

    public override void Up()
    {
        Alter.Table("storage_keys").InSchema(SchemaName)
            .AddColumn("worker_tag").AsString(50).Nullable();
    }

    public override void Down()
    {
        Delete.Column("worker_tag").FromTable("storage_keys").InSchema(SchemaName);
    }
}
