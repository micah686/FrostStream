using Microsoft.Data.Sqlite;
using DataBridge.Persistence.Sqlite;

namespace DataBridge.Persistence.Workflows;

/// <summary>Short, immediate transactions serialize workflow read/modify/write operations across processes.</summary>
internal sealed class SqliteWorkflowDatabase(SqliteConnectionFactory connections)
{
    public Task<T> Read<T>(Func<Session, T> action)
    {
        using var connection = connections.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = action(new Session(connection, transaction));
        transaction.Commit();
        return Task.FromResult(result);
    }

    public Task<T> Write<T>(Func<Session, T> action)
    {
        using var connection = connections.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = action(new Session(connection, transaction));
        transaction.Commit();
        return Task.FromResult(result);
    }

    internal sealed class Session(SqliteConnection connection, SqliteTransaction transaction)
    {
        private SqliteCommand Command(string sql, object?[] values)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (var i = 0; i < values.Length; i++)
                command.Parameters.AddWithValue("$" + (i + 1), values[i] switch
                {
                    null => DBNull.Value,
                    Guid guid => guid.ToString("D"),
                    _ => values[i]!
                });
            return command;
        }
        public int Execute(string sql, params object?[] values)
        {
            using var command = Command(sql, values);
            return command.ExecuteNonQuery();
        }
        public object? Scalar(string sql, params object?[] values)
        {
            using var command = Command(sql, values);
            var result = command.ExecuteScalar();
            return result is DBNull ? null : result;
        }
        public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] values)
        {
            using var command = Command(sql, values);
            using var reader = command.ExecuteReader();
            var results = new List<T>();
            while (reader.Read()) results.Add(map(reader));
            return results;
        }
    }
}
