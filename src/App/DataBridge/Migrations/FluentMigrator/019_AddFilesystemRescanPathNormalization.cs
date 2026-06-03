using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(19, "Add filesystem rescan path normalization")]
public sealed class M019_AddFilesystemRescanPathNormalization : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE OR REPLACE FUNCTION fs_normalize_path(input text)
            RETURNS text
            LANGUAGE sql
            IMMUTABLE
            PARALLEL SAFE
            AS $$
                SELECT CASE
                    WHEN input IS NULL THEN '/'
                    WHEN btrim(regexp_replace(replace(input, E'\\', '/'), '/+', '/', 'g'), '/') = '' THEN '/'
                    ELSE '/' || btrim(regexp_replace(replace(input, E'\\', '/'), '/+', '/', 'g'), '/')
                END;
            $$;
            """);

        Execute.Sql("""
            CREATE INDEX IF NOT EXISTS ix_mciv_storage_key_norm_path
            ON media_content_id_versions (storage_key, fs_normalize_path(storage_path));
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_mciv_storage_key_norm_path;");
        Execute.Sql("DROP FUNCTION IF EXISTS fs_normalize_path(text);");
    }
}
