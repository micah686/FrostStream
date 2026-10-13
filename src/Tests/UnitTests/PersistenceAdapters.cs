using System.Runtime.CompilerServices;
internal static class PersistenceAdapters
{
    [ModuleInitializer]
    internal static void Register()
    {
        DataBridge.Persistence.Postgres.PostgresDialect.Register();
        DataBridge.Persistence.Sqlite.SqliteDialect.Register();
    }
}
