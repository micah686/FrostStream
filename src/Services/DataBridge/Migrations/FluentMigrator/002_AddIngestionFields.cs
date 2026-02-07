using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(2)]
public class AddIngestionFields : Migration
{
    public override void Up()
    {
        Alter.Table("movies")
            .AddColumn("xx_hash").AsString(64).Nullable()
            .AddColumn("file_size_bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("verified").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("storage_connection_string").AsString(2000).Nullable();

        Create.Index("IX_movies_verified")
            .OnTable("movies")
            .OnColumn("verified");
    }

    public override void Down()
    {
        Delete.Index("IX_movies_verified").OnTable("movies");

        Delete.Column("xx_hash").FromTable("movies");
        Delete.Column("file_size_bytes").FromTable("movies");
        Delete.Column("verified").FromTable("movies");
        Delete.Column("storage_connection_string").FromTable("movies");
    }
}
