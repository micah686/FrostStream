using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DataBridge.Lite;

/// <summary>
/// Holds the process-wide Lite ownership lock on one dedicated PostgreSQL session. All dispatch
/// state transitions are executed on that same session, so a disconnected/stale owner cannot
/// commit a completion through a different pooled connection.
/// </summary>
public sealed class LiteExecutionLease(
    NpgsqlDataSource dataSource,
    IOptions<LiteExecutionOptions> options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<LiteExecutionLease> logger) : IHostedService, ILiteExecutionLease, IAsyncDisposable
{
    // "FROSTLIT" as a stable signed 64-bit advisory-lock namespace.
    internal const long AdvisoryLockKey = 0x46524F53544C4954;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _ownershipLost = new();
    private NpgsqlConnection? _connection;
    private Task? _monitor;
    private int _lost;
    private int _disposed;

    public CancellationToken OwnershipLost => _ownershipLost.Token;
    public bool IsOwned => _connection is not null && Volatile.Read(ref _lost) == 0;
    public int? BackendProcessId { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock($1), pg_backend_pid()";
            command.Parameters.AddWithValue(AdvisoryLockKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.GetBoolean(0))
            {
                throw new InvalidOperationException(
                    "Another FrostStream Lite runtime already owns execution for this PostgreSQL database. " +
                    "Stop the other runtime before starting this one.");
            }

            BackendProcessId = reader.GetInt32(1);
            _connection = connection;
            connection = null!;
            logger.LogInformation("Acquired exclusive Lite execution ownership on PostgreSQL backend {BackendPid}.", BackendProcessId);
            _monitor = MonitorOwnershipAsync();
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        SignalOwnershipLost(null);
        if (_monitor is not null)
        {
            try { await _monitor.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        await DisposeConnectionAsync(unlock: true);
    }

    public async Task<T> ExecuteOwnedAsync<T>(
        Func<NpgsqlConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, OwnershipLost);
        await _connectionGate.WaitAsync(linked.Token);
        try
        {
            var connection = _connection;
            if (connection is null || !IsOwned)
                throw new InvalidOperationException("FrostStream Lite no longer owns local execution.");
            return await operation(connection, linked.Token);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            SignalOwnershipLost(exception);
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task MonitorOwnershipAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.OwnershipProbeInterval);
            while (await timer.WaitForNextTickAsync(_ownershipLost.Token))
            {
                await ExecuteOwnedAsync(async (connection, ct) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    await command.ExecuteScalarAsync(ct);
                    return true;
                }, _ownershipLost.Token);
            }
        }
        catch (OperationCanceledException) when (_ownershipLost.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SignalOwnershipLost(exception);
        }
    }

    private void SignalOwnershipLost(Exception? exception)
    {
        if (Interlocked.Exchange(ref _lost, 1) != 0)
            return;

        if (exception is not null)
            logger.LogCritical(exception, "Lost PostgreSQL Lite execution ownership; cancelling local work and stopping.");
        _ownershipLost.Cancel();
        if (exception is not null)
            applicationLifetime.StopApplication();
    }

    private async Task DisposeConnectionAsync(bool unlock)
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (_connection is null)
                return;
            if (unlock && _connection.FullState == System.Data.ConnectionState.Open)
            {
                try
                {
                    await using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT pg_advisory_unlock($1)";
                    command.Parameters.AddWithValue(AdvisoryLockKey);
                    await command.ExecuteScalarAsync();
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not explicitly release the Lite ownership lock; closing the session releases it.");
                }
            }
            await _connection.DisposeAsync();
            _connection = null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static bool IsConnectionFailure(Exception exception)
        => exception is NpgsqlException or IOException or ObjectDisposedException
           || exception.InnerException is NpgsqlException or IOException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        SignalOwnershipLost(null);
        await DisposeConnectionAsync(unlock: false);
        _ownershipLost.Dispose();
        _connectionGate.Dispose();
    }
}
