using DataBridge.Persistence;
using DataBridge.Persistence.Schema;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(98, "Persist download startup generation boundary")]
public sealed class M098_AddDownloadStartupGeneration : Migration
{
    public override void Up() => Execute.Sql(DownloadStartupSchema.Table.CreateSql(PersistenceProvider.Postgres));
    public override void Down() => Delete.Table("download_startup_state").InSchema("jobs");
}
