using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(26, "Create local FrostStream users table")]
public sealed class M026_CreateFrostStreamUsersTable : Migration
{
    private const string SchemaName = "auth";

    public override void Up()
    {
        Create.Schema(SchemaName);

        Create.Table("froststream_users").InSchema(SchemaName)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("authentik_subject_id").AsString(255).NotNullable()
            .WithColumn("display_name").AsString(255).NotNullable()
            .WithColumn("last_seen_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("preferences").AsCustom("jsonb").Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Execute.Sql("""
            CREATE UNIQUE INDEX ux_froststream_users_authentik_subject_id
            ON auth.froststream_users (authentik_subject_id);
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS auth.ux_froststream_users_authentik_subject_id;");
        Delete.Table("froststream_users").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
