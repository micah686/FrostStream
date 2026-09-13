using System.Collections.Concurrent;
using System.Text.Json;
using DataBridge.Lite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Application;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Lite;

public sealed class LocalExecutionDispatcherTests
{
    [Test]
    public async Task Duplicate_Wakeups_Produce_One_Logical_Completion()
    {
        var store = new MemoryStore(Item("fixture", "same"));
        var handler = new TrackingHandler("fixture");
        await using var harness = new DispatcherHarness(store, [handler]);

        await harness.StartAsync();
        for (var index = 0; index < 100; index++)
            harness.Wake.Wake();

        await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        handler.ExecutionCount.ShouldBe(1);
        store.CompletionCount.ShouldBe(1);
    }

    [Test]
    public async Task Download_Concurrency_Defaults_To_One_While_Total_Work_Remains_Bounded()
    {
        var items = Enumerable.Range(0, 4).Select(index => Item("download", $"download/{index}"))
            .Concat(Enumerable.Range(0, 4).Select(index => Item("other", $"other/{index}")))
            .ToArray();
        var store = new MemoryStore(items);
        var total = new ConcurrencyTracker();
        var download = new TrackingHandler("download", delay: TimeSpan.FromMilliseconds(75), total: total);
        var other = new TrackingHandler("other", delay: TimeSpan.FromMilliseconds(75), total: total);
        await using var harness = new DispatcherHarness(store, [download, other], maximumConcurrency: 3);

        await harness.StartAsync();
        await store.AllCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        download.MaximumActive.ShouldBe(1);
        total.MaximumActive.ShouldBeLessThanOrEqualTo(3);
    }

    [Test]
    public async Task Ownership_Loss_Cancels_Active_Work_Without_Stale_Completion()
    {
        var store = new MemoryStore(Item("fixture", "crash"));
        var handler = new TrackingHandler("fixture", blockUntilCancelled: true);
        await using var harness = new DispatcherHarness(store, [handler]);

        await harness.StartAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Lease.LoseOwnership();
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.StopAsync();

        store.CompletionCount.ShouldBe(0);
        store.Status(store.OnlyId).ShouldBe(LocalExecutionStatus.Running);
    }

    [Test]
    public async Task Interrupted_Running_Work_Is_Recovered_By_A_New_Dispatcher()
    {
        var store = new MemoryStore(Item("fixture", "recover"));
        var interrupted = new TrackingHandler("fixture", blockUntilCancelled: true);
        await using (var first = new DispatcherHarness(store, [interrupted]))
        {
            await first.StartAsync();
            await interrupted.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.Lease.LoseOwnership();
            await first.StopAsync();
        }

        var recovered = new TrackingHandler("fixture");
        await using (var second = new DispatcherHarness(store, [recovered]))
        {
            await second.StartAsync();
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        store.RecoveryCount.ShouldBe(2);
        recovered.ExecutionCount.ShouldBe(1);
        store.Status(store.OnlyId).ShouldBe(LocalExecutionStatus.Completed);
    }

    [Test]
    public async Task Active_Cancellation_Reaches_Executor_And_Persists_Cancelled_State()
    {
        var store = new MemoryStore(Item("fixture", "cancel"));
        var handler = new TrackingHandler("fixture", blockUntilCancelled: true);
        await using var harness = new DispatcherHarness(store, [handler]);
        await harness.StartAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await harness.Dispatcher.RequestCancellationAsync(store.OnlyId)).ShouldBeTrue();
        await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        store.Status(store.OnlyId).ShouldBe(LocalExecutionStatus.Cancelled);
    }

    private static LocalExecutionItem Item(string kind, string key)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalExecutionItem(Guid.NewGuid(), kind, key, JsonDocument.Parse("{}").RootElement.Clone(),
            LocalExecutionStatus.Queued, 0, 3, now, now);
    }

    private sealed class DispatcherHarness : IAsyncDisposable
    {
        private readonly global::DataBridge.Lite.LiteExecutionOptions _options;
        private readonly CancellationTokenSource _stopping = new();

        public DispatcherHarness(MemoryStore store, ILocalExecutionHandler[] handlers, int maximumConcurrency = 4)
        {
            _options = new global::DataBridge.Lite.LiteExecutionOptions
            {
                MaximumConcurrency = maximumConcurrency,
                DownloadConcurrency = 1,
                WakeCapacity = 8,
                PollInterval = TimeSpan.FromMilliseconds(20),
                OwnershipProbeInterval = TimeSpan.FromSeconds(1)
            };
            Lease = new FakeLease();
            Wake = new LocalExecutionWakeSignal(Options.Create(_options));
            var snapshots = new SnapshotStore(store);
            var hub = new BoundedLocalProgressHub<LocalExecutionEvent, LocalExecutionSnapshot>(snapshots);
            Dispatcher = new LocalExecutionDispatcher(Lease, store, handlers, Wake, hub,
                Options.Create(_options), NullLogger<LocalExecutionDispatcher>.Instance);
        }

        public FakeLease Lease { get; }
        public LocalExecutionWakeSignal Wake { get; }
        public LocalExecutionDispatcher Dispatcher { get; }

        public Task StartAsync() => Dispatcher.StartAsync(_stopping.Token);

        public async Task StopAsync()
        {
            await _stopping.CancelAsync();
            await Dispatcher.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Dispatcher.Dispose();
            _stopping.Dispose();
            Lease.Dispose();
        }
    }

    private sealed class FakeLease : ILiteExecutionLease, IDisposable
    {
        private readonly CancellationTokenSource _lost = new();
        public CancellationToken OwnershipLost => _lost.Token;
        public bool IsOwned => !_lost.IsCancellationRequested;
        public int? BackendProcessId => 1;
        public void LoseOwnership() => _lost.Cancel();
        public Task<T> ExecuteOwnedAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => _lost.Dispose();
    }

    private sealed class SnapshotStore(MemoryStore store) : IPersistedProgressSnapshotStore<LocalExecutionSnapshot>
    {
        public Task<LocalExecutionSnapshot> LoadAsync(string streamKey, CancellationToken cancellationToken = default)
            => store.LoadSnapshotAsync(streamKey, cancellationToken);
    }

    private sealed class MemoryStore(params LocalExecutionItem[] seed) : ILocalExecutionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, LocalExecutionItem> _items = seed.ToDictionary(item => item.WorkId);
        public TaskCompletionSource Completed { get; } = NewSignal();
        public TaskCompletionSource AllCompleted { get; } = NewSignal();
        public int CompletionCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public Guid OnlyId => _items.Keys.Single();

        public LocalExecutionStatus Status(Guid id) { lock (_gate) return _items[id].Status; }

        public Task<LocalExecutionItem> EnqueueAsync(string kind, string deduplicationKey, JsonElement payload,
            int maximumAttempts = 3, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RecoverInterruptedAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                RecoveryCount++;
                foreach (var (id, item) in _items.ToArray())
                {
                    if (item.Status == LocalExecutionStatus.Running)
                        _items[id] = item with { Status = LocalExecutionStatus.Queued };
                }
            }
            return Task.CompletedTask;
        }

        public Task<LocalExecutionItem?> ClaimNextAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var item = _items.Values.FirstOrDefault(candidate =>
                    candidate.Status is LocalExecutionStatus.Queued or LocalExecutionStatus.RetryWaiting);
                if (item is null)
                    return Task.FromResult<LocalExecutionItem?>(null);
                var claimed = item with { Status = LocalExecutionStatus.Running, Attempt = item.Attempt + 1 };
                _items[item.WorkId] = claimed;
                return Task.FromResult<LocalExecutionItem?>(claimed);
            }
        }

        public Task<LocalExecutionStatus?> CompleteAsync(LocalExecutionItem item, WorkExecutionResult result,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_items.TryGetValue(item.WorkId, out var current) ||
                    current.Status is not (LocalExecutionStatus.Running or LocalExecutionStatus.CancellationRequested))
                    return Task.FromResult<LocalExecutionStatus?>(null);
                var status = result.Disposition == WorkExecutionDisposition.Cancelled
                    ? LocalExecutionStatus.Cancelled
                    : LocalExecutionStatus.Completed;
                _items[item.WorkId] = current with { Status = status };
                CompletionCount++;
                Completed.TrySetResult();
                if (_items.Values.All(value => value.Status == LocalExecutionStatus.Completed))
                    AllCompleted.TrySetResult();
                return Task.FromResult<LocalExecutionStatus?>(status);
            }
        }

        public Task<bool> RequestCancellationAsync(Guid workId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_items.TryGetValue(workId, out var item) || item.Status != LocalExecutionStatus.Running)
                    return Task.FromResult(false);
                _items[workId] = item with { Status = LocalExecutionStatus.CancellationRequested };
                return Task.FromResult(true);
            }
        }

        public Task<LocalExecutionSnapshot> LoadSnapshotAsync(string streamKey,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                return Task.FromResult(new LocalExecutionSnapshot(streamKey, DateTimeOffset.UtcNow, _items.Values.ToArray()));
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TrackingHandler(
        string kind,
        TimeSpan? delay = null,
        bool blockUntilCancelled = false,
        ConcurrencyTracker? total = null) : ILocalExecutionHandler
    {
        private int _active;
        private int _maximumActive;
        private int _executionCount;
        public string Kind => kind;
        public string ConcurrencyGroup => kind;
        public int MaximumActive => _maximumActive;
        public int ExecutionCount => _executionCount;
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource Cancelled { get; } = NewSignal();

        public async Task<WorkExecutionResult> ExecuteAsync(LocalExecutionItem item, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            var active = Interlocked.Increment(ref _active);
            total?.Enter();
            UpdateMaximum(active);
            Started.TrySetResult();
            try
            {
                if (blockUntilCancelled)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                else if (delay is { } duration)
                    await Task.Delay(duration, cancellationToken);
                return new WorkExecutionResult(WorkExecutionDisposition.Completed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                total?.Exit();
            }
        }

        private void UpdateMaximum(int active)
        {
            int observed;
            do
            {
                observed = _maximumActive;
                if (active <= observed) return;
            } while (Interlocked.CompareExchange(ref _maximumActive, active, observed) != observed);
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ConcurrencyTracker
    {
        private int _active;
        private int _maximumActive;
        public int MaximumActive => _maximumActive;

        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = _maximumActive;
                if (active <= observed) return;
            } while (Interlocked.CompareExchange(ref _maximumActive, active, observed) != observed);
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }
}
