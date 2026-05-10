using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(4, "Create dedicated 'cleipnir' schema for Cleipnir.Flows runtime tables")]
public sealed class M004_CreateCleipnirSchema : Migration
{
    // Cleipnir.Flows.PostgresSql does not expose a schema option — only tablePrefix. We
    // isolate its tables by giving the runtime its own connection string with
    // 'Search Path=cleipnir,public', so every DDL/DML it emits resolves into this schema.
    // FluentMigrator and EF Core continue to use 'public'.
    public override void Up()
        => Execute.Sql("CREATE SCHEMA IF NOT EXISTS cleipnir;");

    public override void Down()
        => Execute.Sql("DROP SCHEMA IF EXISTS cleipnir CASCADE;");
}
