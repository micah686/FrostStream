using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataBridge.Data;
using DataBridge.Persistence.Queries;
using DataBridge.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Persistence;

/// <summary>Explicit, operation-specific provider statements. SQL is selected, never rewritten at runtime.</summary>
internal static class ApplicationQueries
{
    private static readonly IReadOnlyDictionary<string, ProviderQuery> Catalog = ApplicationQueryCatalog.Statements;
    private static readonly HashSet<string> Tables = LoadTables();

    public static string Render(PersistenceProvider provider, string operation, object?[] fragments)
    {
        var query = Catalog.TryGetValue(operation, out var found) ? found : throw new InvalidOperationException($"Unknown database operation '{operation}'.");
        var sql = provider == PersistenceProvider.Postgres ? query.Postgres : query.Sqlite;
        for (var index = 0; index < fragments.Length; index++)
            sql = sql.Replace($"{{fs{index}}}", Convert.ToString(fragments[index], CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return sql;
    }

    public static FormattableString Parameters(DataBridgeDbContext db, string operation, object?[] values)
    {
        var provider = db.Database.IsSqlite() ? PersistenceProvider.Sqlite : PersistenceProvider.Postgres;
        var sql = Render(provider, operation, []);
        for (var index = 0; index < values.Length; index++) sql = sql.Replace($"{{fs{index}}}", $"{{{index}}}", StringComparison.Ordinal);
        return FormattableStringFactory.Create(sql, provider == PersistenceProvider.Sqlite ? values.Select(ApplicationParameters.SqliteValue).ToArray() : values);
    }

    public static string Table(PersistenceProvider provider, string logicalName)
    {
        if (!Tables.Contains(logicalName)) throw new InvalidOperationException($"Unknown application table '{logicalName}'.");
        var parts = logicalName.Split('.');
        return provider == PersistenceProvider.Postgres ? logicalName : SqliteStorageEncoding.TableName(parts[0], parts[1]);
    }

    private static HashSet<string> LoadTables()
    {
        using var manifest = JsonDocument.Parse(SqliteBaseline.ManifestJson);
        return manifest.RootElement.GetProperty("tables").EnumerateArray().Select(table => table.GetProperty("schema").GetString() + "." + table.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
    }
}

internal static class ApplicationContextQueries
{
    public static string Sql(this DataBridgeDbContext db, string operation, params object?[] fragments)
        => ApplicationQueries.Render(db.Database.IsSqlite() ? PersistenceProvider.Sqlite : PersistenceProvider.Postgres, operation, fragments);
    public static FormattableString ParameterizedSql(this DataBridgeDbContext db, string operation, params object?[] values)
        => ApplicationQueries.Parameters(db, operation, values);
}
