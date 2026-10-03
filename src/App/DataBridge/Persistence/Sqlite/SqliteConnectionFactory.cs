using Microsoft.Data.Sqlite;

namespace DataBridge.Persistence.Sqlite;

/// <summary>All raw/EF/workflow connections must use this setup, including reopened EF connections.</summary>
public sealed class SqliteConnectionFactory
{
    private readonly PersistenceOptions options;
    internal string ConnectionString { get; }

    public SqliteConnectionFactory(PersistenceOptions options)
    {
        if (options.Provider != PersistenceProvider.Sqlite)
            throw new ArgumentException("SQLite connection setup requires the SQLite provider.", nameof(options));
        if (options.BusyTimeoutSeconds is < 1 or > 60 || !Path.IsPathFullyQualified(options.SqlitePath))
            throw new ArgumentException("SQLite requires an absolute file path and a bounded timeout.", nameof(options));
        this.options = options;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.SqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            DefaultTimeout = options.BusyTimeoutSeconds,
            Pooling = false
        }.ToString();
    }

    internal void EnsureDirectory() => Directory.CreateDirectory(Path.GetDirectoryName(options.SqlitePath)!);

    public SqliteConnection OpenConnection(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            connection.Open();
            ConfigureOpenConnection(connection, cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // SQLite has synchronous I/O even behind ADO.NET's async APIs. Keep setup in one path;
    // this convenience entry point does not imply a background thread or asynchronous disk I/O.
    public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(OpenConnection(cancellationToken));

    internal void ConfigureOpenConnection(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connection.DefaultTimeout = options.BusyTimeoutSeconds;
        connection.CreateFunction<string?, string?, string, bool?>("fs_ilike", (value, pattern, escape) => value is null || pattern is null ? null : PersistenceFunctions.ILike(value, pattern, escape), isDeterministic: true);
        connection.CreateFunction<string?, string?>("fs_guid_text", value => value is null ? null : Guid.Parse(value).ToString("D"), isDeterministic: true);
        connection.CreateFunction<long?, long?>("fs_epoch_round", value => value is null ? null : checked((long)Math.Round(value.Value / 1000000m, MidpointRounding.AwayFromZero)), isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        // Microsoft.Data.Sqlite retries busy/locked commands up to CommandTimeout. A small native
        // wait avoids spinning; do not set a second multi-second wait or use unbounded timeout=0.
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=100;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var journalMode = (string?)command.ExecuteScalar();
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite WAL could not be enabled (mode '{journalMode}').");
        cancellationToken.ThrowIfCancellationRequested();
    }
}
