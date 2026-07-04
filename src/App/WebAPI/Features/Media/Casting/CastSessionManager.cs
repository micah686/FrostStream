using System.Collections.Concurrent;
using System.Threading.Channels;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;

namespace WebAPI.Features.Media.Casting;

/// <summary>
/// Singleton owner of server-side cast sessions: one live receiver connection per device, driven
/// through <see cref="ICastSessionClient"/>. Transport commands are serialized per session, status
/// pushes from the receiver update a last-known snapshot, and snapshots fan out to SSE subscribers
/// through per-subscriber bounded channels (drop-oldest, same shape as the download queue hub).
/// When the receiver drops the connection the session is torn down and subscribers get a final
/// <c>ended</c> frame — there is no silent auto-reconnect; the user re-casts.
/// </summary>
public sealed class CastSessionManager(
    ICastDeviceLocator locator,
    ICastSessionClientFactory clientFactory,
    ILogger<CastSessionManager> logger) : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CastSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new(StringComparer.Ordinal);

    private sealed class CastSession(string deviceId, CastDeviceDto device, ICastSessionClient client)
    {
        public string DeviceId { get; } = deviceId;
        public CastDeviceDto Device { get; } = device;
        public ICastSessionClient Client { get; } = client;
        public SemaphoreSlim CommandLock { get; } = new(1, 1);
        public Lock SnapshotLock { get; } = new();
        public ConcurrentDictionary<Guid, Channel<CastSessionEvent>> Subscribers { get; } = new();

        // Both are immutable records swapped atomically; readers always see a consistent value.
        public volatile CastSessionSnapshot Snapshot = new()
        {
            PlayerState = "Loading",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        public volatile SessionMedia Media = new(Guid.Empty, "", DateTimeOffset.MinValue, null);
    }

    public sealed record SessionMedia(Guid MediaGuid, string Title, DateTimeOffset TokenExpiresAt, double? DurationHint);

    public async Task<CastSessionDto> StartAsync(string deviceId, CastLoadSpec spec, CancellationToken cancellationToken)
    {
        var startLock = _startLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await startLock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(deviceId, out var existing))
            {
                await LoadOnSessionAsync(existing, spec, cancellationToken);
                return ToDto(existing);
            }

            var receiver = await locator.FindReceiverAsync(deviceId, cancellationToken)
                ?? throw new CastDeviceNotFoundException(deviceId);
            var device = (await locator.GetDevicesAsync(refresh: false, cancellationToken))
                .FirstOrDefault(candidate => candidate.Id == deviceId)
                ?? throw new CastDeviceNotFoundException(deviceId);

            var client = clientFactory.Create();
            var session = new CastSession(deviceId, device, client);
            client.MediaStatusChanged += (_, status) => OnMediaStatus(session, status);
            client.ReceiverStatusChanged += (_, status) => OnReceiverStatus(session, status);
            client.Disconnected += (_, _) => OnDisconnected(session);

            try
            {
                await GuardAsync(client.ConnectAsync(receiver), "connect to", device, cancellationToken);
                await GuardAsync(client.LaunchDefaultReceiverAsync(), "launch the media receiver on", device, cancellationToken);
                await LoadOnSessionAsync(session, spec, cancellationToken);
            }
            catch
            {
                await DisposeClientQuietlyAsync(session);
                throw;
            }

            _sessions[deviceId] = session;
            logger.LogInformation(
                "Started cast session on {DeviceName} ({DeviceId}) for media {MediaGuid}.",
                device.Name, deviceId, spec.MediaGuid);
            return ToDto(session);
        }
        finally
        {
            startLock.Release();
        }
    }

    public IReadOnlyList<CastSessionDto> ListSessions()
        => _sessions.Values.Select(ToDto).ToArray();

    public CastSessionDto? GetSession(string deviceId)
        => _sessions.TryGetValue(deviceId, out var session) ? ToDto(session) : null;

    public Task<CastSessionDto> PlayAsync(string deviceId, CancellationToken cancellationToken)
        => ExecuteMediaCommandAsync(deviceId, client => client.PlayAsync(), "resume playback on", cancellationToken);

    public Task<CastSessionDto> PauseAsync(string deviceId, CancellationToken cancellationToken)
        => ExecuteMediaCommandAsync(deviceId, client => client.PauseAsync(), "pause playback on", cancellationToken);

    public Task<CastSessionDto> StopAsync(string deviceId, CancellationToken cancellationToken)
        => ExecuteMediaCommandAsync(deviceId, client => client.StopAsync(), "stop playback on", cancellationToken);

    public Task<CastSessionDto> SeekAsync(string deviceId, double seconds, CancellationToken cancellationToken)
        => ExecuteMediaCommandAsync(deviceId, client => client.SeekAsync(seconds), "seek on", cancellationToken);

    public async Task<CastSessionDto> SetVolumeAsync(
        string deviceId,
        double? level,
        bool? muted,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(deviceId);
        await session.CommandLock.WaitAsync(cancellationToken);
        try
        {
            if (level is { } newLevel)
            {
                await GuardAsync(session.Client.SetVolumeAsync(newLevel), "set the volume on", session.Device, cancellationToken);
            }

            if (muted is { } newMuted)
            {
                await GuardAsync(session.Client.SetMutedAsync(newMuted), "set mute on", session.Device, cancellationToken);
            }
        }
        finally
        {
            session.CommandLock.Release();
        }

        // The receiver confirms via a status push; reflect the request optimistically meanwhile.
        UpdateSnapshot(session, snapshot => snapshot with
        {
            VolumeLevel = level ?? snapshot.VolumeLevel,
            Muted = muted ?? snapshot.Muted,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return ToDto(session);
    }

    /// <summary>Tears the session down: stops the receiver connection and completes all SSE streams.</summary>
    public async Task DisconnectAsync(string deviceId)
    {
        var session = RequireSession(deviceId);
        _sessions.TryRemove(new KeyValuePair<string, CastSession>(deviceId, session));
        EndSession(session);

        try
        {
            await session.Client.DisconnectAsync().WaitAsync(CommandTimeout);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disconnect from {DeviceId} failed; disposing the client anyway.", deviceId);
        }

        await DisposeClientQuietlyAsync(session);
    }

    /// <summary>
    /// Subscribes to a session's status stream. The current snapshot is queued immediately so the
    /// subscriber renders without waiting for the next receiver push. Returns null when the device
    /// has no active session.
    /// </summary>
    public (Guid Id, ChannelReader<CastSessionEvent> Reader)? Subscribe(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
        {
            return null;
        }

        var channel = Channel.CreateBounded<CastSessionEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
        var id = Guid.NewGuid();
        session.Subscribers[id] = channel;
        channel.Writer.TryWrite(new CastSessionEvent(CastSessionEvent.StatusEvent, ToDto(session)));
        return (id, channel.Reader);
    }

    public void Unsubscribe(string deviceId, Guid subscriptionId)
    {
        if (_sessions.TryGetValue(deviceId, out var session) &&
            session.Subscribers.TryRemove(subscriptionId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (deviceId, session) in _sessions)
        {
            _sessions.TryRemove(new KeyValuePair<string, CastSession>(deviceId, session));
            EndSession(session);
            await DisposeClientQuietlyAsync(session);
        }
    }

    private async Task LoadOnSessionAsync(CastSession session, CastLoadSpec spec, CancellationToken cancellationToken)
    {
        await session.CommandLock.WaitAsync(cancellationToken);
        try
        {
            session.Media = new SessionMedia(spec.MediaGuid, spec.Title, spec.TokenExpiresAt, spec.Media.Duration);
            UpdateSnapshot(session, snapshot => new CastSessionSnapshot
            {
                PlayerState = "Loading",
                CurrentTime = spec.StartPositionSeconds ?? 0,
                DurationSeconds = spec.Media.Duration,
                VolumeLevel = snapshot.VolumeLevel,
                Muted = snapshot.Muted,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var status = await GuardAsync(
                session.Client.LoadAsync(spec.Media, spec.ActiveTrackIds),
                "load media on",
                session.Device,
                cancellationToken);
            status ??= await RequireMediaStatusAsync(session, "confirm the loaded media session on", cancellationToken);
            ApplyMediaStatus(session, status);

            // The load request has no start position, so seek right after the media is loaded.
            if (spec.StartPositionSeconds is { } position && position > 0)
            {
                var seeked = await GuardAsync(
                    session.Client.SeekAsync(position),
                    "seek on",
                    session.Device,
                    cancellationToken);
                ApplyMediaStatus(session, seeked);
            }
        }
        finally
        {
            session.CommandLock.Release();
        }

        Fan(session, CastSessionEvent.StatusEvent);
    }

    private async Task<CastSessionDto> ExecuteMediaCommandAsync(
        string deviceId,
        Func<ICastSessionClient, Task<MediaStatus?>> command,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(deviceId);
        await session.CommandLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureMediaSessionAsync(session, cancellationToken);
            var status = await GuardAsync(command(session.Client), actionDescription, session.Device, cancellationToken);
            status ??= await RequireMediaStatusAsync(session, "refresh media status on", cancellationToken);
            ApplyMediaStatus(session, status);
        }
        finally
        {
            session.CommandLock.Release();
        }

        Fan(session, CastSessionEvent.StatusEvent);
        return ToDto(session);
    }

    private CastSession RequireSession(string deviceId)
        => _sessions.TryGetValue(deviceId, out var session) ? session : throw new CastSessionNotFoundException(deviceId);

    private async Task EnsureMediaSessionAsync(CastSession session, CancellationToken cancellationToken)
    {
        if (session.Client.HasMediaSession)
        {
            return;
        }

        await RequireMediaStatusAsync(session, "refresh media status on", cancellationToken);
    }

    private async Task<MediaStatus> RequireMediaStatusAsync(
        CastSession session,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        var status = await GuardAsync(
            session.Client.GetMediaStatusAsync(),
            actionDescription,
            session.Device,
            cancellationToken);

        if (status is null)
        {
            throw new CastDeviceUnreachableException(
                $"No media session is active on '{session.Device.Name}'. Start casting the media again.");
        }

        ApplyMediaStatus(session, status);
        return status;
    }

    private async Task<T> GuardAsync<T>(
        Task<T> command,
        string actionDescription,
        CastDeviceDto device,
        CancellationToken cancellationToken)
    {
        try
        {
            return await command.WaitAsync(CommandTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new CastDeviceUnreachableException($"Timed out trying to {actionDescription} '{device.Name}'.", ex);
        }
        catch (Exception ex)
        {
            throw new CastDeviceUnreachableException($"Failed to {actionDescription} '{device.Name}': {ex.Message}", ex);
        }
    }

    private Task GuardAsync(
        Task command,
        string actionDescription,
        CastDeviceDto device,
        CancellationToken cancellationToken)
        => GuardAsync(WrapAsync(command), actionDescription, device, cancellationToken);

    private static async Task<bool> WrapAsync(Task task)
    {
        await task;
        return true;
    }

    private void OnMediaStatus(CastSession session, MediaStatus? status)
    {
        if (status is null)
        {
            return;
        }

        ApplyMediaStatus(session, status);
        Fan(session, CastSessionEvent.StatusEvent);
    }

    private void OnReceiverStatus(CastSession session, ChromecastStatus? status)
    {
        if (status?.Volume is null)
        {
            return;
        }

        UpdateSnapshot(session, snapshot => snapshot with
        {
            VolumeLevel = status.Volume.Level ?? snapshot.VolumeLevel,
            Muted = status.Volume.Muted ?? snapshot.Muted,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        Fan(session, CastSessionEvent.StatusEvent);
    }

    private void OnDisconnected(CastSession session)
    {
        // Remove only if this exact session still owns the slot — a replacement session for the
        // same device must not be torn down by the old connection's disconnect event.
        var removed = _sessions.TryRemove(new KeyValuePair<string, CastSession>(session.DeviceId, session));
        logger.LogInformation(
            "Cast device {DeviceName} ({DeviceId}) disconnected{Suffix}.",
            session.Device.Name, session.DeviceId, removed ? "" : " (session already replaced)");
        EndSession(session);
        _ = DisposeClientQuietlyAsync(session);
    }

    private void EndSession(CastSession session)
    {
        UpdateSnapshot(session, snapshot => snapshot with
        {
            PlayerState = "Disconnected",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        Fan(session, CastSessionEvent.EndedEvent);

        foreach (var (id, channel) in session.Subscribers)
        {
            if (session.Subscribers.TryRemove(id, out _))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    private void ApplyMediaStatus(CastSession session, MediaStatus? status)
    {
        if (status is null)
        {
            return;
        }

        UpdateSnapshot(session, snapshot => new CastSessionSnapshot
        {
            PlayerState = status.PlayerState.ToString(),
            CurrentTime = status.CurrentTime,
            DurationSeconds = status.Media?.Duration ?? snapshot.DurationSeconds ?? session.Media.DurationHint,
            VolumeLevel = status.Volume?.Level ?? snapshot.VolumeLevel,
            Muted = status.Volume?.Muted ?? snapshot.Muted,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static void UpdateSnapshot(CastSession session, Func<CastSessionSnapshot, CastSessionSnapshot> update)
    {
        // Single-writer-at-a-time is not guaranteed (events vs commands), so swap under a lock.
        lock (session.SnapshotLock)
        {
            session.Snapshot = update(session.Snapshot);
        }
    }

    private void Fan(CastSession session, string eventName)
    {
        if (session.Subscribers.IsEmpty)
        {
            return;
        }

        var evt = new CastSessionEvent(eventName, ToDto(session));
        foreach (var (id, channel) in session.Subscribers)
        {
            if (!channel.Writer.TryWrite(evt))
            {
                logger.LogTrace("Dropped cast event for device {DeviceId} (subscriber {Id} channel full).", session.DeviceId, id);
            }
        }
    }

    private static CastSessionDto ToDto(CastSession session)
    {
        var media = session.Media;
        return new CastSessionDto
        {
            DeviceId = session.DeviceId,
            DeviceName = session.Device.Name,
            MediaGuid = media.MediaGuid,
            Title = media.Title,
            Snapshot = session.Snapshot,
            TokenExpiresAt = media.TokenExpiresAt
        };
    }

    private async Task DisposeClientQuietlyAsync(CastSession session)
    {
        try
        {
            await session.Client.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disposing cast client for {DeviceId} failed.", session.DeviceId);
        }
    }
}
