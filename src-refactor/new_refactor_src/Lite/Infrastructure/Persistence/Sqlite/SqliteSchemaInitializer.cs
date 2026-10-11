using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NodaTime;
using Quartz;

namespace DataBridge.Persistence.Sqlite;

internal sealed class SqliteSchemaInitializer(SqliteConnectionFactory factory, IClock clock, IReadOnlyList<SqliteSchemaMigration>? migrations = null) : IApplicationSchemaInitializer
{
    // Includes seeds as well as DDL. A released baseline must not silently change under an existing DB.
    internal static string Checksum { get; } = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(SqliteBaseline.Sql + "\n" + SqliteBaseline.ManifestJson)));

    public void Initialize(CancellationToken cancellationToken = default)
    {
        InitializeBaseline(cancellationToken);
        using var connection = factory.OpenConnection(cancellationToken);
        SqliteSchemaMigrations.Apply(connection, migrations ?? SqliteSchemaMigrations.All, cancellationToken, clock.GetCurrentInstant());
    }

    private void InitializeBaseline(CancellationToken cancellationToken)
    {
        using var connection = factory.OpenConnection(cancellationToken);
        // Acquire writer ownership before checking the version. Racing initializers either see the
        // committed baseline or fail within the configured busy budget; DDL/seeds/version commit together.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{SqliteBaseline.HistoryTable}'";
        var hasHistory = (long)command.ExecuteScalar()! != 0;
        if (!hasHistory)
        {
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            if ((long)command.ExecuteScalar()! != 0)
                throw new InvalidOperationException("SQLite database has tables but no FrostStream migration history. Refusing to adopt an unversioned database.");
        }
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {SqliteBaseline.HistoryTable} (
                version INTEGER NOT NULL PRIMARY KEY,
                checksum TEXT NOT NULL,
                applied_at INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        command.CommandText = $"SELECT version, checksum FROM {SqliteBaseline.HistoryTable} ORDER BY version;";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetInt32(0) != SqliteBaseline.Version || reader.GetString(1) != Checksum)
                    throw new InvalidOperationException("SQLite migration history is newer, unknown or has a different baseline checksum. Use a compatible application version.");
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                return;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        command.CommandText = SqliteBaseline.Sql;
        command.ExecuteNonQuery();
        var now = clock.GetCurrentInstant();
        InsertSeeds(connection, transaction, now, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        command.CommandText = $"INSERT INTO {SqliteBaseline.HistoryTable} (version, checksum, applied_at) VALUES (@version, @checksum, @now);";
        command.Parameters.AddWithValue("@version", SqliteBaseline.Version);
        command.Parameters.AddWithValue("@checksum", Checksum);
        command.Parameters.AddWithValue("@now", SqliteStorageEncoding.ToUnixMicroseconds(now));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void InsertSeeds(SqliteConnection connection, SqliteTransaction transaction, Instant now, CancellationToken cancellationToken)
    {
        using var manifest = JsonDocument.Parse(SqliteBaseline.ManifestJson);
        foreach (var seed in manifest.RootElement.GetProperty("seeds").EnumerateArray())
        {
            var tableName = SqliteStorageEncoding.TableName(seed.GetProperty("schema").GetString()!, seed.GetProperty("table").GetString()!);
            foreach (var row in seed.GetProperty("rows").EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var columns = row.EnumerateObject().ToArray();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"INSERT INTO {Quote(tableName)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) " +
                    $"VALUES ({string.Join(", ", columns.Select((_, index) => $"@p{index}"))});";
                for (var index = 0; index < columns.Length; index++)
                    command.Parameters.AddWithValue($"@p{index}", SeedValue(columns[index].Value, row, now));
                command.ExecuteNonQuery();
            }
        }
    }

    private static object SeedValue(JsonElement value, JsonElement row, Instant now) => value.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.Number => value.GetInt64(),
        JsonValueKind.String when value.GetString() == "$now" => SqliteStorageEncoding.ToUnixMicroseconds(now),
        JsonValueKind.String when value.GetString() == "$next-cron" => NextCron(row, now),
        JsonValueKind.String => value.GetString()!,
        _ => throw new InvalidOperationException("Unsupported SQLite baseline seed value.")
    };

    private static object NextCron(JsonElement row, Instant now)
    {
        var cron = CronExpression.Parse(row.GetProperty("cron").GetString()!)
            .WithTimeZone(TimeZoneInfo.FindSystemTimeZoneById(row.GetProperty("timezone").GetString()!));
        var next = cron.GetNextValidTimeAfter(now.ToDateTimeOffset());
        return next is null ? DBNull.Value : SqliteStorageEncoding.ToUnixMicroseconds(Instant.FromDateTimeOffset(next.Value));
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
