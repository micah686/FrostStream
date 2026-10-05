using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Drops the worker-local <c>avatar_cache_path</c>/<c>banner_cache_path</c> columns from
/// creator_sources. Durable avatar/banner blob paths now live in <c>metadata.accounts</c>
/// (avatar_storage_path/banner_storage_path + storage_key), which is the authoritative table
/// the read/search/rescan surfaces consume. creator_sources retains the source URL + content
/// hash for change detection plus the refresh-state columns.
/// </summary>
[Migration(24, "Drop deprecated avatar/banner cache_path columns from creator_sources")]
public sealed class M024_DropCreatorSourceCachePathColumns : Migration
{
    public override void Up()
    {
        Delete.Column("avatar_cache_path")
            .Column("banner_cache_path")
            .FromTable("creator_sources").InSchema("discovery");
    }

    public override void Down()
    {
        Alter.Table("creator_sources").InSchema("discovery")
            .AddColumn("avatar_cache_path").AsString(1024).Nullable()
            .AddColumn("banner_cache_path").AsString(1024).Nullable();
    }
}
