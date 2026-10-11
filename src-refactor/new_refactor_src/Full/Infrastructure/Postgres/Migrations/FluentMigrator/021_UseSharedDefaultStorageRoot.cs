using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(21, "Use the shared storage root for the seeded default local storage")]
public sealed class M021_UseSharedDefaultStorageRoot : Migration
{
    public override void Up()
    {
        Execute.Sql(
            """
            UPDATE storage.storage_keys_local AS local
            SET path = '${FROSTSTREAM_STORAGE_ROOT}'
            FROM storage.storage_keys AS sk
            WHERE local.storage_key_id = sk.id
              AND sk.key = 'default'
              AND local.path IN ('./data', './data/', 'data', 'data/');
            """);
    }

    public override void Down()
    {
        Execute.Sql(
            """
            UPDATE storage.storage_keys_local AS local
            SET path = './data/'
            FROM storage.storage_keys AS sk
            WHERE local.storage_key_id = sk.id
              AND sk.key = 'default'
              AND local.path = '${FROSTSTREAM_STORAGE_ROOT}';
            """);
    }
}
