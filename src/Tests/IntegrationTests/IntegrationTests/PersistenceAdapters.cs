using System.Runtime.CompilerServices;
internal static class PersistenceAdapters
{
    [ModuleInitializer]
    internal static void Register()
    {
        global::DataBridge.Persistence.Postgres.PostgresDialect.Register();
        global::DataBridge.Persistence.Sqlite.SqliteDialect.Register();
    }
}
