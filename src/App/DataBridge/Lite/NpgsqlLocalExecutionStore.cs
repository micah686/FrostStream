using System.Text.Json;
using Npgsql;
using Shared.Application;

namespace DataBridge.Lite;

public sealed class NpgsqlLocalExecutionStore(
    ILiteExecutionLease lease,
    NpgsqlDataSource dataSource) : ILocalExecutionStore, IPersistedProgressSnapshotStore<LocalExecutionSnapshot>
{
    public Task<LocalExecutionItem> EnqueueAsync(
        string kind,
        string deduplicationKey,
        JsonElement payload,
        int maximumAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        return lease.ExecuteOwnedAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO jobs.local_execution_work
                    (work_id, kind, deduplication_key, payload, status, maximum_attempts)
                VALUES ($1, $2, $3, $4::jsonb, 'queued', $5)
                ON CONFLICT (deduplication_key) DO UPDATE
                    SET deduplication_key = EXCLUDED.deduplication_key
                RETURNING work_id, kind, deduplication_key, payload::text, status, attempt,
                          maximum_attempts, available_at, updated_at, error_code, error_message
                """;
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(kind);
            command.Parameters.AddWithValue(deduplicationKey);
            command.Parameters.AddWithValue(payload.GetRawText());
            command.Parameters.AddWithValue(maximumAttempts);
            return await ReadOneAsync(command, ct);
        }, cancellationToken);
    }

    public Task RecoverInterruptedAsync(CancellationToken cancellationToken = default)
        => lease.ExecuteOwnedAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs.local_execution_work
                SET status = CASE WHEN status = 'cancellation_requested' THEN 'cancelled' ELSE 'queued' END,
                    available_at = now(), updated_at = now(),
                    error_code = CASE WHEN status = 'running' THEN 'process_interrupted' ELSE error_code END,
                    error_message = CASE WHEN status = 'running' THEN 'Recovered after the previous Lite runtime stopped.' ELSE error_message END,
                    completed_at = CASE WHEN status = 'cancellation_requested' THEN now() ELSE completed_at END
                WHERE status IN ('running', 'cancellation_requested')
                """;
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, cancellationToken);

    public Task<LocalExecutionItem?> ClaimNextAsync(CancellationToken cancellationToken = default)
        => lease.ExecuteOwnedAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH candidate AS (
                    SELECT work_id
                    FROM jobs.local_execution_work
                    WHERE status IN ('queued', 'retry_waiting') AND available_at <= now()
                    ORDER BY available_at, created_at, work_id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                UPDATE jobs.local_execution_work AS work
                SET status = 'running', attempt = attempt + 1, updated_at = now(),
                    error_code = NULL, error_message = NULL
                FROM candidate
                WHERE work.work_id = candidate.work_id
                RETURNING work.work_id, work.kind, work.deduplication_key, work.payload::text,
                          work.status, work.attempt, work.maximum_attempts, work.available_at,
                          work.updated_at, work.error_code, work.error_message
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Read(reader) : null;
        }, cancellationToken);

    public Task<LocalExecutionStatus?> CompleteAsync(
        LocalExecutionItem item,
        WorkExecutionResult result,
        CancellationToken cancellationToken = default)
        => lease.ExecuteOwnedAsync<LocalExecutionStatus?>(async (connection, ct) =>
        {
            var (status, retryAt) = Completion(item, result);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs.local_execution_work
                SET status = CASE WHEN status = 'cancellation_requested' THEN 'cancelled' ELSE $2 END,
                    available_at = $3, updated_at = now(),
                    completed_at = CASE WHEN status = 'cancellation_requested' OR $2 IN ('completed', 'cancelled', 'failed') THEN now() ELSE NULL END,
                    error_code = $4, error_message = $5
                WHERE work_id = $1 AND status IN ('running', 'cancellation_requested')
                RETURNING status
                """;
            command.Parameters.AddWithValue(item.WorkId);
            command.Parameters.AddWithValue(StatusText(status));
            command.Parameters.AddWithValue(retryAt);
            command.Parameters.AddWithValue((object?)result.ErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)result.ErrorMessage ?? DBNull.Value);
            var persisted = await command.ExecuteScalarAsync(ct);
            return persisted is string text ? ParseStatus(text) : null;
        }, cancellationToken);

    public Task<bool> RequestCancellationAsync(Guid workId, CancellationToken cancellationToken = default)
        => lease.ExecuteOwnedAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs.local_execution_work
                SET status = CASE WHEN status IN ('queued', 'retry_waiting') THEN 'cancelled' ELSE 'cancellation_requested' END,
                    completed_at = CASE WHEN status IN ('queued', 'retry_waiting') THEN now() ELSE completed_at END,
                    updated_at = now()
                WHERE work_id = $1 AND status IN ('queued', 'retry_waiting', 'running')
                """;
            command.Parameters.AddWithValue(workId);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }, cancellationToken);

    public async Task<LocalExecutionSnapshot> LoadSnapshotAsync(
        string streamKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        await using var command = dataSource.CreateCommand("""
            SELECT work_id, kind, deduplication_key, payload::text, status, attempt,
                   maximum_attempts, available_at, updated_at, error_code, error_message
            FROM jobs.local_execution_work
            WHERE ($1 = 'all' OR work_id = CASE WHEN $1 ~* '^[0-9a-f-]{36}$' THEN $1::uuid ELSE NULL END)
            ORDER BY updated_at DESC, work_id
            LIMIT 200
            """);
        command.Parameters.AddWithValue(streamKey);
        var items = new List<LocalExecutionItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(Read(reader));
        return new LocalExecutionSnapshot(streamKey, DateTimeOffset.UtcNow, items);
    }

    Task<LocalExecutionSnapshot> IPersistedProgressSnapshotStore<LocalExecutionSnapshot>.LoadAsync(
        string streamKey,
        CancellationToken cancellationToken) => LoadSnapshotAsync(streamKey, cancellationToken);

    private static (LocalExecutionStatus Status, DateTimeOffset AvailableAt) Completion(
        LocalExecutionItem item,
        WorkExecutionResult result)
    {
        var now = DateTimeOffset.UtcNow;
        return result.Disposition switch
        {
            WorkExecutionDisposition.Completed or WorkExecutionDisposition.AlreadyCompleted
                => (LocalExecutionStatus.Completed, now),
            WorkExecutionDisposition.Cancelled => (LocalExecutionStatus.Cancelled, now),
            WorkExecutionDisposition.Retry when item.Attempt < item.MaximumAttempts
                => (LocalExecutionStatus.RetryWaiting, now + (result.RetryAfter ?? TimeSpan.FromSeconds(5))),
            WorkExecutionDisposition.Retry => (LocalExecutionStatus.Failed, now),
            _ => (LocalExecutionStatus.Failed, now)
        };
    }

    private static async Task<LocalExecutionItem> ReadOneAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("PostgreSQL did not return the local work row.");
        return Read(reader);
    }

    private static LocalExecutionItem Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
        JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(),
        ParseStatus(reader.GetString(4)), reader.GetInt32(5), reader.GetInt32(6), reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static LocalExecutionStatus ParseStatus(string value) => value switch
    {
        "queued" => LocalExecutionStatus.Queued,
        "running" => LocalExecutionStatus.Running,
        "retry_waiting" => LocalExecutionStatus.RetryWaiting,
        "cancellation_requested" => LocalExecutionStatus.CancellationRequested,
        "completed" => LocalExecutionStatus.Completed,
        "cancelled" => LocalExecutionStatus.Cancelled,
        "failed" => LocalExecutionStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown local execution status '{value}'.")
    };

    private static string StatusText(LocalExecutionStatus status) => status switch
    {
        LocalExecutionStatus.Queued => "queued",
        LocalExecutionStatus.Running => "running",
        LocalExecutionStatus.RetryWaiting => "retry_waiting",
        LocalExecutionStatus.CancellationRequested => "cancellation_requested",
        LocalExecutionStatus.Completed => "completed",
        LocalExecutionStatus.Cancelled => "cancelled",
        LocalExecutionStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
