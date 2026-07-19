using FluentMigrator;
using System.Data;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(59, "Create metadata account external id aliases")]
public sealed class M059_CreateAccountExternalIds : Migration
{
    private const string SchemaName = "metadata";

    public override void Up()
    {
        Create.Table("account_external_ids").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("account_id").AsInt64().NotNullable()
            .WithColumn("platform").AsString(50).NotNullable()
            .WithColumn("id_kind").AsString(50).NotNullable()
            .WithColumn("external_id").AsCustom("text").NotNullable();

        Create.ForeignKey("fk_account_external_ids_account_id")
            .FromTable("account_external_ids").InSchema(SchemaName).ForeignColumn("account_id")
            .ToTable("accounts").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ux_account_external_ids_platform_value")
            .OnTable("account_external_ids").InSchema(SchemaName)
            .OnColumn("platform").Ascending()
            .OnColumn("external_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_account_external_ids_account_id")
            .OnTable("account_external_ids").InSchema(SchemaName)
            .OnColumn("account_id").Ascending();

        Execute.Sql("""
            INSERT INTO metadata.account_external_ids (account_id, platform, id_kind, external_id)
            SELECT id, platform, 'legacy_account_handle', account_handle
            FROM metadata.accounts
            WHERE account_handle IS NOT NULL
              AND btrim(account_handle) <> ''
            ON CONFLICT (platform, external_id) DO NOTHING;
            """);

        Execute.Sql("""
            UPDATE metadata.media_comments mc
            SET account_id = mm.account_id
            FROM metadata.media_metadata mm
            WHERE mc.media_guid = mm.media_guid
              AND mc.is_uploader = true
              AND mc.account_id <> mm.account_id;
            """);
    }

    public override void Down()
    {
        Delete.Table("account_external_ids").InSchema(SchemaName);
    }
}
