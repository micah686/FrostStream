namespace DataBridge.Persistence.Schema;

/// <summary>Portable column types; provider-specific operations such as enum DDL stay in their own history.</summary>
internal enum SchemaColumnType { Text, Integer, Timestamp }
internal sealed record SchemaColumn(string Name, SchemaColumnType Type, bool Nullable = false);
internal sealed record SchemaTable(string Schema, string Name, IReadOnlyList<SchemaColumn> Columns, string PrimaryKey)
{
    public string CreateSql(PersistenceProvider provider)
    {
        var table = provider == PersistenceProvider.Sqlite ? Quote(Schema + "_" + Name) : Quote(Schema) + "." + Quote(Name);
        var columns = Columns.Select(c => Quote(c.Name) + " " + (c.Type switch
        {
            SchemaColumnType.Text => "TEXT",
            SchemaColumnType.Integer => "INTEGER",
            SchemaColumnType.Timestamp => provider == PersistenceProvider.Sqlite ? "INTEGER" : "TIMESTAMP WITH TIME ZONE",
            _ => throw new ArgumentOutOfRangeException()
        }) + (c.Nullable ? "" : " NOT NULL"));
        return $"CREATE TABLE {table} ({string.Join(", ", columns)}, PRIMARY KEY({Quote(PrimaryKey)}));";
    }
    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}

internal static class DownloadStartupSchema
{
    public static readonly SchemaTable Table = new("jobs", "download_startup_state",
        [new("name", SchemaColumnType.Text), new("generation_started_at", SchemaColumnType.Timestamp)], "name");
}
