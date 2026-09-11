using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(98, "Create durable application initialization state")]
public sealed class M098_CreateInitializationState : Migration
{
    public override void Up()
    {
        Create.Schema("froststream");
        Create.Table("initialization_state").InSchema("froststream")
            .WithColumn("component").AsString(64).PrimaryKey()
            .WithColumn("version").AsInt32().NotNullable()
            .WithColumn("completed_at").AsCustom("timestamp with time zone").NotNullable();
    }

    public override void Down()
    {
        Delete.Table("initialization_state").InSchema("froststream");
        Delete.Schema("froststream");
    }
}
