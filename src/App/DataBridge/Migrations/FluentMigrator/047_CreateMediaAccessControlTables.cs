using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Adds watch-time access control independent of the OpenFGA endpoint authorization (Axis 1). These
/// tables gate which media a user may actually play, evaluated by DataBridge against the caller's
/// Authentik group claims:
/// <list type="bullet">
/// <item><c>media_access_restrictions</c> — per-media allow-list of groups. Rows present ⇒ only members
/// of a listed group may watch; no rows ⇒ unrestricted.</item>
/// <item><c>provider_access_restrictions</c> — per yt-dlp provider (extractor) allow-list of groups
/// (e.g. block <c>pornhub</c> for everyone but an <c>adults</c> group).</item>
/// <item><c>age_limit_policies</c> — tiered allow-list keyed by minimum age limit. Only the highest tier
/// at or below a media item's age_limit applies.</item>
/// </list>
/// None carry a foreign key to the media tables (the media rows live in other schemas); the
/// per-media rows are cleaned up explicitly when media is deleted.
/// </summary>
[Migration(47, "Create media access control tables")]
public sealed class M047_CreateMediaAccessControlTables : Migration
{
    private const string SchemaName = "auth";

    public override void Up()
    {
        Create.Table("media_access_restrictions").InSchema(SchemaName)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("media_guid").AsGuid().NotNullable()
            .WithColumn("group_name").AsString(255).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by_subject").AsString(255).Nullable();

        Execute.Sql("""
            CREATE UNIQUE INDEX ux_media_access_restrictions_media_group
            ON auth.media_access_restrictions (media_guid, group_name);

            CREATE INDEX ix_media_access_restrictions_media
            ON auth.media_access_restrictions (media_guid);
            """);

        Create.Table("provider_access_restrictions").InSchema(SchemaName)
            .WithColumn("provider_pattern").AsString(255).NotNullable()
            .WithColumn("group_name").AsString(255).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by_subject").AsString(255).Nullable();

        Execute.Sql("""
            ALTER TABLE auth.provider_access_restrictions
            ADD CONSTRAINT pk_provider_access_restrictions PRIMARY KEY (provider_pattern, group_name);
            """);

        Create.Table("age_limit_policies").InSchema(SchemaName)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("minimum_age_limit").AsInt32().NotNullable()
            .WithColumn("group_name").AsString(255).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by_subject").AsString(255).Nullable();

        Execute.Sql("""
            CREATE UNIQUE INDEX ux_age_limit_policies_threshold_group
            ON auth.age_limit_policies (minimum_age_limit, group_name);
            """);
    }

    public override void Down()
    {
        Delete.Table("age_limit_policies").InSchema(SchemaName);
        Delete.Table("provider_access_restrictions").InSchema(SchemaName);

        Execute.Sql("DROP INDEX IF EXISTS auth.ix_media_access_restrictions_media;");
        Execute.Sql("DROP INDEX IF EXISTS auth.ux_media_access_restrictions_media_group;");
        Delete.Table("media_access_restrictions").InSchema(SchemaName);
    }
}
