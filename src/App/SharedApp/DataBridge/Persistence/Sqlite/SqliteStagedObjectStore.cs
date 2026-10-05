using FrostStream.ApplicationContracts;

namespace DataBridge.Persistence.Sqlite;

/// <summary>Small staged manifests, isolated by bucket in the application database.</summary>
public sealed class SqliteStagedObjectStore : IStagedObjectStore
{
    private readonly SqliteConnectionFactory factory;
    private readonly string bucket;

    public SqliteStagedObjectStore(SqliteConnectionFactory factory, string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        this.factory = factory;
        this.bucket = bucket;
    }

    public async Task<string> PutAsync(string key, Stream data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var buffer = new MemoryStream();
        await data.CopyToAsync(buffer, cancellationToken);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO staged_objects(bucket,object_key,data) VALUES($bucket,$key,$data) ON CONFLICT(bucket,object_key) DO UPDATE SET data=excluded.data";
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$data", buffer.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return key;
    }

    public async Task GetAsync(string key, Stream target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM staged_objects WHERE bucket=$bucket AND object_key=$key";
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$key", key);
        var data = await command.ExecuteScalarAsync(cancellationToken) as byte[]
            ?? throw new KeyNotFoundException($"Staged object '{bucket}/{key}' was not found.");
        await target.WriteAsync(data, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM staged_objects WHERE bucket=$bucket AND object_key=$key";
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
