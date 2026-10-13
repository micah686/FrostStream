using DataBridge.Persistence.Sqlite;
using DataBridge.Search;

namespace FrostStream.Lite.Infrastructure.Search;

internal sealed class SqliteSearchActivationBarrier(SqliteConnectionFactory connections) : ISearchActivationBarrier
{
    public async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var connection = connections.OpenConnection(deadline.Token);
        using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE, WAL readers remain available.
        await action(deadline.Token);
        await transaction.CommitAsync(deadline.Token);
    }
}
