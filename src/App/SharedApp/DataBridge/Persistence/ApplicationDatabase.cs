using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DataBridge.Persistence.Sqlite;
using Npgsql;

namespace DataBridge.Persistence;

/// <summary>Connection/command boundary for shared raw-SQL operations. Does not own the injected stores.</summary>
public sealed class ApplicationDatabase
{
    private readonly NpgsqlDataSource? postgres;
    private readonly SqliteConnectionFactory? sqlite;
    public PersistenceProvider Provider { get; }

    public ApplicationDatabase(NpgsqlDataSource postgres)
    {
        this.postgres = postgres;
        Provider = PersistenceProvider.Postgres;
    }

    public ApplicationDatabase(SqliteConnectionFactory sqlite)
    {
        this.sqlite = sqlite;
        Provider = PersistenceProvider.Sqlite;
    }

    public string Table(string logicalName) => ApplicationQueries.Table(Provider, logicalName);

    public string Sql(string operation, params object?[] fragments) => ApplicationQueries.Render(Provider, operation, fragments);

    public DbCommand CreateCommand(string sql)
    {
        if (postgres is not null) return postgres.CreateCommand(sql);
        var connection = sqlite!.OpenConnection();
        try { return ApplicationDbCommands.Create(connection, sql, ownsConnection: true); }
        catch { connection.Dispose(); throw; }
    }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => postgres is not null ? await postgres.OpenConnectionAsync(cancellationToken) : await sqlite!.OpenConnectionAsync(cancellationToken);
}

public static class ApplicationDbCommands
{
    public static string Sql(this DbConnection connection, string operation, params object?[] fragments)
        => ApplicationQueries.Render(connection is NpgsqlConnection ? PersistenceProvider.Postgres : PersistenceProvider.Sqlite, operation, fragments);

    public static string Table(this DbConnection connection, string logicalName)
        => ApplicationQueries.Table(connection is NpgsqlConnection ? PersistenceProvider.Postgres : PersistenceProvider.Sqlite, logicalName);

    public static DbCommand Create(string sql, DbConnection connection, DbTransaction? transaction = null) => Create(connection, sql, transaction);

    public static DbCommand Create(DbConnection connection, string sql, DbTransaction? transaction = null, bool ownsConnection = false)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return new ApplicationCommand(command, ownsConnection);
    }

    private sealed class ApplicationCommand(DbCommand inner, bool ownsConnection) : DbCommand
    {
        private readonly DbConnection? ownedConnection = ownsConnection ? inner.Connection : null;
        [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout
        {
            get => inner.CommandTimeout;
            set => inner.CommandTimeout = inner.Connection is Microsoft.Data.Sqlite.SqliteConnection sqlite ? (value == 0 ? sqlite.DefaultTimeout : Math.Min(value, sqlite.DefaultTimeout)) : value;
        }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        public override void Cancel() => inner.Cancel();
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => inner.ExecuteNonQueryAsync(cancellationToken);
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => inner.ExecuteScalarAsync(cancellationToken);
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
            => inner.ExecuteReaderAsync(behavior, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); ownedConnection?.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            if (ownedConnection is not null) await ownedConnection.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
