using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(45, "Add provider-specific query limits to creator sources")]
public sealed class M045_AddCreatorSourceProviderQueryLimits : Migration
{
    public override void Up()
    {
        Alter.Table("creator_sources").InSchema("discovery")
            .AddColumn("provider_query_limits_json").AsCustom("jsonb").Nullable();

        Execute.Sql(
            "ALTER TABLE discovery.creator_sources ADD CONSTRAINT ck_creator_sources_provider_query_limits_json_object " +
            "CHECK (provider_query_limits_json IS NULL OR jsonb_typeof(provider_query_limits_json) = 'object');");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE discovery.creator_sources DROP CONSTRAINT IF EXISTS ck_creator_sources_provider_query_limits_json_object;");
        Delete.Column("provider_query_limits_json").FromTable("creator_sources").InSchema("discovery");
    }
}
