using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Adds a <c>storage_key</c> column (the storage backend identifier, e.g. "default") to the
/// sidecar-bearing metadata tables so the filesystem rescan can scope each sidecar to the
/// backend it actually lives on. Existing rows are left NULL: pre-migration thumbnail/caption
/// values hold remote URLs (not durable blob keys), and avatar/banner paths were never written.
/// </summary>
[Migration(23, "Add storage_key to media_metadata, media_captions, and accounts for sidecar scoping")]
public sealed class M023_AddSidecarStorageKeyColumns : Migration
{
    private const string SchemaName = "metadata";

    public override void Up()
    {
        Alter.Table("media_metadata").InSchema(SchemaName)
            .AddColumn("storage_key").AsString(255).Nullable();

        Alter.Table("media_captions").InSchema(SchemaName)
            .AddColumn("storage_key").AsString(255).Nullable();

        Alter.Table("accounts").InSchema(SchemaName)
            .AddColumn("storage_key").AsString(255).Nullable();
    }

    public override void Down()
    {
        Delete.Column("storage_key").FromTable("media_metadata").InSchema(SchemaName);
        Delete.Column("storage_key").FromTable("media_captions").InSchema(SchemaName);
        Delete.Column("storage_key").FromTable("accounts").InSchema(SchemaName);
    }
}
