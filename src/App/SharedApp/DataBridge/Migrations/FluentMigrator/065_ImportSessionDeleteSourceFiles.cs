using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(65, "Add import session delete-source-files option")]
public sealed class M065_ImportSessionDeleteSourceFiles : Migration
{
    public override void Up()
    {
        Alter.Table("import_sessions").InSchema("imports")
            .AddColumn("delete_source_files").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("delete_source_files").FromTable("import_sessions").InSchema("imports");
    }
}
