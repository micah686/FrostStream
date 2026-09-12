using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Shared.Application;

/// <summary>Loads the authoritative persisted state used to seed a reconnecting SSE client.</summary>
public interface IPersistedProgressSnapshotStore<TSnapshot>
{
    Task<TSnapshot> LoadAsync(string streamKey, CancellationToken cancellationToken = default);
}

public interface ILocalProgressHub<TEvent, TSnapshot>
{
    Task<LocalProgressSubscription<TEvent, TSnapshot>> SubscribeAsync(
        string streamKey,
        int capacity = 256,
        CancellationToken cancellationToken = default);

    void Publish(string streamKey, TEvent progressEvent);
}

public sealed class LocalProgressSubscription<TEvent, TSnapshot> : IAsyncDisposable
{
    private readonly Action _unsubscribe;
    private int _disposed;

    internal LocalProgressSubscription(TSnapshot snapshot, ChannelReader<TEvent> events, Action unsubscribe)
    {
        Snapshot = snapshot;
        Events = events;
        _unsubscribe = unsubscribe;
    }

    public TSnapshot Snapshot { get; }
    public ChannelReader<TEvent> Events { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _unsubscribe();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Bounded live hints layered over an authoritative persisted snapshot. Dropped frames are safe:
/// reconnecting consumers always reload the snapshot instead of treating this hub as durable state.
/// </summary>
public sealed class BoundedLocalProgressHub<TEvent, TSnapshot>(
    IPersistedProgressSnapshotStore<TSnapshot> snapshotStore) : ILocalProgressHub<TEvent, TSnapshot>
{
    private const int MaximumCapacity = 4096;
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public async Task<LocalProgressSubscription<TEvent, TSnapshot>> SubscribeAsync(
        string streamKey,
        int capacity = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);

        var channel = Channel.CreateBounded<TEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(streamKey, channel);

        try
        {
            var snapshot = await snapshotStore.LoadAsync(streamKey, cancellationToken);
            return new LocalProgressSubscription<TEvent, TSnapshot>(
                snapshot,
                channel.Reader,
                () => Unsubscribe(id));
        }
        catch
        {
            Unsubscribe(id);
            throw;
        }
    }

    public void Publish(string streamKey, TEvent progressEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        foreach (var subscriber in _subscribers.Values)
        {
            if (StringComparer.Ordinal.Equals(subscriber.StreamKey, streamKey))
                subscriber.Channel.Writer.TryWrite(progressEvent);
        }
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
            subscriber.Channel.Writer.TryComplete();
    }

    private sealed record Subscriber(string StreamKey, Channel<TEvent> Channel);
}
