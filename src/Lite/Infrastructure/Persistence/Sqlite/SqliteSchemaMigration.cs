using System.Security.Cryptography;
using System.Text;
using DataBridge.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace DataBridge.Persistence.Sqlite;

internal sealed record SqliteSchemaMigration(int Version, string Name, string Sql, bool RebuildsTables = false)
{
    public string Checksum => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}\n{Name}\n{Sql}")));
}

internal static class SqliteSchemaMigrations
{
    // Append only. Released baseline and upgrade checksums must remain unchanged.
    public static IReadOnlyList<SqliteSchemaMigration> All { get; } =
        [new(2, "Persist download startup generation boundary", DownloadStartupSchema.Table.CreateSql(PersistenceProvider.Sqlite)),
         new(3, "Persist staged import objects", """
             CREATE TABLE staged_objects (
                 bucket TEXT NOT NULL,
                 object_key TEXT NOT NULL,
                 data BLOB NOT NULL,
                 PRIMARY KEY (bucket, object_key)
             );
             """),
         new(4, "Persist encrypted local secrets", """
             CREATE TABLE local_secrets (
                 path TEXT NOT NULL PRIMARY KEY,
                 payload BLOB NOT NULL
             );
             """),
         new(5, "Persist per-worker download capacity", """
             CREATE TABLE jobs_worker_capacities (
                 worker_id TEXT NOT NULL PRIMARY KEY,
                 max_active_jobs INTEGER NOT NULL CHECK (max_active_jobs BETWEEN 1 AND 32)
             );
             ALTER TABLE jobs_download_jobs ADD COLUMN assigned_worker_id TEXT;
             CREATE INDEX ix_download_jobs_assigned_worker_id ON jobs_download_jobs(assigned_worker_id);
             """),
         new(6, "Reserve worker capacity for collection discovery", """
             CREATE TABLE jobs_collection_worker_leases (
                 dispatch_id TEXT NOT NULL PRIMARY KEY,
                 worker_id TEXT NOT NULL,
                 group_id TEXT,
                 expires_at INTEGER NOT NULL,
                 released_at INTEGER
             );
             CREATE INDEX ix_jobs_collection_worker_leases_worker_id_released_at_expires_at
                 ON jobs_collection_worker_leases(worker_id, released_at, expires_at);
             """)];

    /// <summary>Explicit rebuild descriptions must supply the full new table DDL and recreate all indexes/triggers.</summary>
    public static string Rebuild(string table, IReadOnlyList<string> preservedColumns, string createReplacementSql,
        IReadOnlyList<string> indexesAndTriggers)
    {
        var replacement = table + "_upgrade";
        var columns = string.Join(",", preservedColumns.Select(SchemaTable.Quote));
        // createReplacementSql names the replacement explicitly; never rewrite arbitrary SQL.
        return $"{createReplacementSql}\nINSERT INTO {SchemaTable.Quote(replacement)} ({columns}) SELECT {columns} FROM {SchemaTable.Quote(table)};\n"
            + $"DROP TABLE {SchemaTable.Quote(table)};\nALTER TABLE {SchemaTable.Quote(replacement)} RENAME TO {SchemaTable.Quote(table)};\n"
            + string.Join("\n", indexesAndTriggers);
    }

    public static void Apply(SqliteConnection connection, IReadOnlyList<SqliteSchemaMigration> migrations, CancellationToken ct, NodaTime.Instant? appliedAt = null)
    {
        // SQLite cannot disable FKs inside a transaction. Rebuilds need this outside their transaction
        // so DROP TABLE cannot cascade away child rows. Validate all FKs before committing the pending upgrades.
        using var command = connection.CreateCommand();
        var rebuild = migrations.Any(m => m.RebuildsTables);
        if (rebuild) { command.CommandText = "PRAGMA foreign_keys=OFF;"; command.ExecuteNonQuery(); }
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            command.Transaction = transaction;
            command.CommandText = $"SELECT version,checksum FROM {SqliteBaseline.HistoryTable} ORDER BY version";
            var history = new List<(int Version, string Checksum)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) history.Add((reader.GetInt32(0),reader.GetString(1)));
            if (history.Count == 0 || history[0] != (1, SqliteSchemaInitializer.Checksum))
                throw new InvalidOperationException("SQLite has an unknown or changed baseline history.");
            if (!migrations.Select(x => x.Version).SequenceEqual(Enumerable.Range(2,migrations.Count)))
                throw new InvalidOperationException("SQLite migrations must be ordered and contiguous after the released baseline.");
            for (var i=1; i<history.Count; i++)
                if (i > migrations.Count || history[i] != (migrations[i-1].Version,migrations[i-1].Checksum))
                    throw new InvalidOperationException("SQLite migration history is newer, unknown or has a different upgrade checksum. Use a compatible application version.");
            foreach (var migration in migrations.Skip(history.Count-1))
            {
                ct.ThrowIfCancellationRequested();
                command.CommandText = migration.Sql;
                command.Parameters.Clear();
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA foreign_key_check";
                using (var reader = command.ExecuteReader())
                    if (reader.Read()) throw new InvalidOperationException($"SQLite migration {migration.Version} would violate foreign keys.");
                ct.ThrowIfCancellationRequested();
                command.CommandText = $"INSERT INTO {SqliteBaseline.HistoryTable}(version,checksum,applied_at) VALUES($version,$checksum,$now)";
                command.Parameters.AddWithValue("$version",migration.Version);
                command.Parameters.AddWithValue("$checksum",migration.Checksum);
                command.Parameters.AddWithValue("$now",SqliteStorageEncoding.ToUnixMicroseconds(appliedAt ?? NodaTime.SystemClock.Instance.GetCurrentInstant()));
                command.ExecuteNonQuery();
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally
        {
            command.Transaction = null;
            command.Parameters.Clear();
            command.CommandText = "PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }
    }
}
