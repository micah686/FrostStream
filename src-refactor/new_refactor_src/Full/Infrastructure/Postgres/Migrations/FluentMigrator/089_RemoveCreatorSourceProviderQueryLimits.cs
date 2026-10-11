using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(89, "Remove creator source provider query limits")]
public sealed class M089_RemoveCreatorSourceProviderQueryLimits : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE discovery.creator_sources DROP CONSTRAINT IF EXISTS ck_creator_sources_provider_query_limits_json_object;");
        Delete.Column("provider_query_limits_json").FromTable("creator_sources").InSchema("discovery");
    }

    public override void Down()
    {
        Alter.Table("creator_sources").InSchema("discovery")
            .AddColumn("provider_query_limits_json").AsCustom("jsonb").Nullable();

        Execute.Sql(
            "ALTER TABLE discovery.creator_sources ADD CONSTRAINT ck_creator_sources_provider_query_limits_json_object " +
            "CHECK (provider_query_limits_json IS NULL OR jsonb_typeof(provider_query_limits_json) = 'object');");
    }
}
