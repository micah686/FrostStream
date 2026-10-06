using System.Text.Json;

namespace DataBridge.Persistence.Sqlite;

internal static class SqliteBaseline
{
    public const int Version = 1;
    public const int PostgresVersion = 97;
    public const string HistoryTable = "froststream_schema_versions";
    public const string CurrentTimestampSql = "(CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000)";
    public static string Sql { get; } = ReadResource("001-baseline.sql");
    public static string ManifestJson { get; } = ReadResource("postgres-v97-manifest.json");

    public static string ReadResource(string name)
    {
        using var stream = typeof(SqliteBaseline).Assembly.GetManifestResourceStream($"DataBridge.Persistence.Sqlite.Schema.{name}")
            ?? throw new InvalidOperationException($"Missing SQLite baseline resource '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyDictionary<string, string[]> EnumLabels { get; } = ReadEnums();

    private static Dictionary<string, string[]> ReadEnums()
    {
        using var document = JsonDocument.Parse(ManifestJson);
        return document.RootElement.GetProperty("enums").EnumerateObject().ToDictionary(
            property => property.Name, property => property.Value.EnumerateArray().Select(label => label.GetString()!).ToArray());
    }
}
