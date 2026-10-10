using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(64, "Add import wizard metadata source and fetch state")]
public sealed class M064_ReworkImportWizard : Migration
{
    public override void Up()
    {
        Alter.Table("import_session_items").InSchema("imports")
            .AddColumn("metadata_source").AsString(32).NotNullable().WithDefaultValue("Placeholder")
            .AddColumn("metadata_fetch_state").AsString(32).NotNullable().WithDefaultValue("NotAttempted")
            .AddColumn("metadata_fetch_attempt").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("metadata_fetch_message").AsString(4096).Nullable();

        Execute.Sql("""
            UPDATE imports.import_session_items
            SET metadata_source = CASE
                WHEN enriched_metadata IS NOT NULL THEN 'YtDlp'
                WHEN user_metadata IS NOT NULL THEN 'ManualMapping'
                WHEN sidecars->>'infoJson' IS NOT NULL THEN 'YtDlp'
                WHEN sidecars->>'nfo' IS NOT NULL THEN 'Nfo'
                ELSE 'Placeholder'
            END,
            metadata_fetch_state = CASE
                WHEN enriched_metadata IS NOT NULL THEN 'Succeeded'
                WHEN sidecars->>'infoJson' IS NOT NULL THEN 'Succeeded'
                WHEN error_code LIKE 'enrich%' THEN 'Failed'
                ELSE 'NotAttempted'
            END,
            metadata_fetch_message = CASE
                WHEN sidecars->>'infoJson' IS NOT NULL THEN 'info.json found'
                ELSE metadata_fetch_message
            END;
            """);
    }

    public override void Down()
    {
        Delete.Column("metadata_fetch_message").FromTable("import_session_items").InSchema("imports");
        Delete.Column("metadata_fetch_attempt").FromTable("import_session_items").InSchema("imports");
        Delete.Column("metadata_fetch_state").FromTable("import_session_items").InSchema("imports");
        Delete.Column("metadata_source").FromTable("import_session_items").InSchema("imports");
    }
}
