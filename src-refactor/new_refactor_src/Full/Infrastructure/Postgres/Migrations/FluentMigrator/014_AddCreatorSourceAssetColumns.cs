using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(14, "Add channel asset cache columns to creator_sources")]
public sealed class M014_AddCreatorSourceAssetColumns : Migration
{
    public override void Up()
    {
        Alter.Table("creator_sources").InSchema("discovery")
            .AddColumn("avatar_url").AsString(4096).Nullable()
            .AddColumn("avatar_cache_path").AsString(1024).Nullable()
            .AddColumn("avatar_content_hash").AsString(64).Nullable()
            .AddColumn("banner_url").AsString(4096).Nullable()
            .AddColumn("banner_cache_path").AsString(1024).Nullable()
            .AddColumn("banner_content_hash").AsString(64).Nullable()
            .AddColumn("assets_last_refreshed_at").AsCustom("timestamp with time zone").Nullable()
            .AddColumn("assets_last_attempt_at").AsCustom("timestamp with time zone").Nullable()
            .AddColumn("assets_attempt_count").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("assets_last_error").AsString(2048).Nullable();
    }

    public override void Down()
    {
        Delete.Column("avatar_url")
            .Column("avatar_cache_path")
            .Column("avatar_content_hash")
            .Column("banner_url")
            .Column("banner_cache_path")
            .Column("banner_content_hash")
            .Column("assets_last_refreshed_at")
            .Column("assets_last_attempt_at")
            .Column("assets_attempt_count")
            .Column("assets_last_error")
            .FromTable("creator_sources").InSchema("discovery");
    }
}
