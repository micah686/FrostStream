using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(27, "Create cookie profile metadata table")]
public sealed class M027_CreateCookieProfilesTable : Migration
{
    private const string SchemaName = "auth";

    public override void Up()
    {
        // The `auth` schema already exists (M026). Only non-secret metadata lives here; cookie
        // content itself is stored in OpenBAO under cookies/users/{ownerSubject}/{profileKey}.
        Create.Table("cookie_profiles").InSchema(SchemaName)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("owner_subject").AsString(255).NotNullable()
            .WithColumn("profile_key").AsString(100).NotNullable()
            .WithColumn("site").AsString(255).Nullable()
            .WithColumn("display_name").AsString(255).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Execute.Sql("""
            CREATE UNIQUE INDEX ux_cookie_profiles_owner_profile
            ON auth.cookie_profiles (owner_subject, profile_key);
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS auth.ux_cookie_profiles_owner_profile;");
        Delete.Table("cookie_profiles").InSchema(SchemaName);
    }
}
