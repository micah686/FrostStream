using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Application;

namespace DataBridge.Lite;

public interface ILocalExecutionWakeSignal
{
    void Wake();
}

public sealed class LocalExecutionWakeSignal : ILocalExecutionWakeSignal
{
    private readonly Channel<bool> _channel;

    public LocalExecutionWakeSignal(IOptions<LiteExecutionOptions> options)
    {
        _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(options.Value.WakeCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void Wake() => _channel.Writer.TryWrite(true);
    internal ChannelReader<bool> Reader => _channel.Reader;
}

/// <summary>
/// Converts bounded, lossy wake-ups into claims against the authoritative PostgreSQL ledger.
/// Wake-ups are only hints: periodic polling and startup recovery guarantee eventual dispatch.
/// </summary>
public sealed class LocalExecutionDispatcher(
    ILiteExecutionLease lease,
    ILocalExecutionStore store,
    IEnumerable<ILocalExecutionHandler> handlers,
    LocalExecutionWakeSignal wakeSignal,
    ILocalProgressHub<LocalExecutionEvent, LocalExecutionSnapshot> progressHub,
    IOptions<LiteExecutionOptions> options,
    ILogger<LocalExecutionDispatcher> logger) : BackgroundService
{
    private readonly IReadOnlyDictionary<string, ILocalExecutionHandler> _handlers = handlers
        .GroupBy(handler => handler.Kind, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => group.Count() == 1
                ? group.Single()
                : throw new InvalidOperationException($"Multiple local execution handlers are registered for '{group.Key}'."),
            StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupGates = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.OwnershipLost);
        var ct = ownership.Token;
        await store.RecoverInterruptedAsync(ct);
        wakeSignal.Wake();

        var running = new HashSet<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                running.RemoveWhere(task => task.IsCompleted);
                while (running.Count < options.Value.MaximumConcurrency)
                {
                    var item = await store.ClaimNextAsync(ct);
                    if (item is null)
                        break;
                    var execution = ExecuteItemAsync(item, ct);
                    running.Add(execution);
                }

                if (running.Count >= options.Value.MaximumConcurrency)
                {
                    await Task.WhenAny(running).WaitAsync(ct);
                    continue;
                }

                using var poll = new CancellationTokenSource(options.Value.PollInterval);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, poll.Token);
                try { await wakeSignal.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (poll.IsCancellationRequested && !ct.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            foreach (var active in _active.Values)
                active.Cancel();
            try { await Task.WhenAll(running); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    public async Task<bool> RequestCancellationAsync(Guid workId, CancellationToken cancellationToken = default)
    {
        var changed = await store.RequestCancellationAsync(workId, cancellationToken);
        if (changed && _active.TryGetValue(workId, out var active))
            active.Cancel();
        wakeSignal.Wake();
        return changed;
    }

    private async Task ExecuteItemAsync(LocalExecutionItem item, CancellationToken ownershipToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ownershipToken);
        _active[item.WorkId] = execution;
        WorkExecutionResult result;
        var handler = _handlers.GetValueOrDefault(item.Kind);
        var group = handler?.ConcurrencyGroup ?? item.Kind;
        var groupGate = _groupGates.GetOrAdd(group, name => new SemaphoreSlim(GroupLimit(name), GroupLimit(name)));
        try
        {
            await groupGate.WaitAsync(execution.Token);
            try
            {
                Publish(item, LocalExecutionStatus.Running);
                result = handler is null
                    ? new WorkExecutionResult(WorkExecutionDisposition.Failed, "handler_unavailable",
                        $"No local execution handler is registered for '{item.Kind}'.")
                    : await handler.ExecuteAsync(item, execution.Token);
            }
            finally
            {
                groupGate.Release();
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            if (ownershipToken.IsCancellationRequested)
                return; // A new owner recovers this still-running row; this owner must not commit.
            result = new WorkExecutionResult(WorkExecutionDisposition.Cancelled, "cancelled", "Execution was cancelled.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Local execution {WorkId} ({Kind}) failed.", item.WorkId, item.Kind);
            result = new WorkExecutionResult(WorkExecutionDisposition.Retry, "executor_failure",
                "The local executor failed; inspect the Lite runtime log for details.");
        }
        finally
        {
            _active.TryRemove(item.WorkId, out _);
        }

        if (!ownershipToken.IsCancellationRequested)
        {
            var persistedStatus = await store.CompleteAsync(item, result, ownershipToken);
            if (persistedStatus is { } status)
                Publish(item, status, result.ErrorCode, result.ErrorMessage);
        }
    }

    private int GroupLimit(string group)
        => StringComparer.OrdinalIgnoreCase.Equals(group, "download")
            ? options.Value.DownloadConcurrency
            : options.Value.MaximumConcurrency;

    private void Publish(LocalExecutionItem item, LocalExecutionStatus status, string? code = null, string? message = null)
    {
        var evt = new LocalExecutionEvent(item.WorkId, item.Kind, status, item.Attempt, DateTimeOffset.UtcNow, code, message);
        progressHub.Publish("all", evt);
        progressHub.Publish(item.WorkId.ToString(), evt);
    }

}
