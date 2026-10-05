using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(39, "Create owner-scoped searchable notes")]
public sealed class M039_CreateUserNotes : Migration
{
    private const string NotesSchema = "notes";

    public override void Up()
    {
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
        Create.Schema(NotesSchema);

        Create.Table("user_notes").InSchema(NotesSchema)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("owner_subject").AsString(255).NotNullable()
            .WithColumn("target_type").AsString(32).NotNullable()
            .WithColumn("target_id").AsString(64).NotNullable()
            .WithColumn("note").AsString(8192).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.UniqueConstraint("ux_user_notes_owner_target")
            .OnTable("user_notes").WithSchema(NotesSchema)
            .Columns("owner_subject", "target_type", "target_id");

        Create.Index("ix_user_notes_owner_updated_at")
            .OnTable("user_notes").InSchema(NotesSchema)
            .OnColumn("owner_subject").Ascending()
            .OnColumn("updated_at").Descending();

        Execute.Sql("""
            CREATE INDEX ix_user_notes_note_trgm
            ON notes.user_notes
            USING gin (note gin_trgm_ops);
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS notes.ix_user_notes_note_trgm;");
        Delete.Index("ix_user_notes_owner_updated_at").OnTable("user_notes").InSchema(NotesSchema);
        Delete.UniqueConstraint("ux_user_notes_owner_target").FromTable("user_notes").InSchema(NotesSchema);
        Delete.Table("user_notes").InSchema(NotesSchema);
        Delete.Schema(NotesSchema);
    }
}
