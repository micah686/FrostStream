using Npgsql;

namespace DataBridge.Initialization;

public interface IInitializationStateStore
{
    Task<int?> ReadVersionAsync(CancellationToken cancellationToken);
    Task ClearSuccessAsync(CancellationToken cancellationToken);
    Task RecordSuccessAsync(int version, CancellationToken cancellationToken);
}

public sealed class PostgresInitializationStateStore(NpgsqlDataSource dataSource) : IInitializationStateStore
{
    public async Task<int?> ReadVersionAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT version
            FROM froststream.initialization_state
            WHERE component = 'application'
            """);
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? null : Convert.ToInt32(result);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return null;
        }
    }

    public async Task RecordSuccessAsync(int version, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO froststream.initialization_state (component, version, completed_at)
            VALUES ('application', $1, now())
            ON CONFLICT (component) DO UPDATE
            SET version = EXCLUDED.version, completed_at = EXCLUDED.completed_at
            """);
        command.Parameters.AddWithValue(version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearSuccessAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM froststream.initialization_state WHERE component = 'application'
            """);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            // Expected on a truly empty installation, before the migration step creates the table.
        }
    }
}
