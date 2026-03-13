using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(1, "Create required PostgreSQL extensions")]
public class M001_CreateExtensions : Migration
{
    public override void Up()
    {
        // Enable UUID extension
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
        
        // Enable trigram extension for fuzzy text search
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS \"pg_trgm\";");
    }

    public override void Down()
    {
        Execute.Sql("DROP EXTENSION IF EXISTS \"pg_trgm\";");
        Execute.Sql("DROP EXTENSION IF EXISTS \"uuid-ossp\";");
    }
}
