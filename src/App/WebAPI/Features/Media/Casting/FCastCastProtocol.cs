using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FCast.SenderSDK;
using Microsoft.Extensions.Options;
using FcastMetadata = FCast.SenderSDK.Metadata;

namespace WebAPI.Features.Media.Casting;

/// <summary>FCast receiver discovery and command adapter backed by FCastSenderSDKDotnet.</summary>
public sealed class FCastCastProtocol(
    IOptions<CastingOptions> options,
    ILogger<FCastCastProtocol> logger,
    ILoggerFactory loggerFactory) : ICastProtocol
{
    private readonly CastDiscoveryCache<(CastDeviceDto Device, DeviceInfo Info)> _cache =
        new(TimeSpan.FromSeconds(Math.Max(1, options.Value.DeviceCacheSeconds)));

    public string Id => CastProtocolIds.FCast;

    public bool Enabled => options.Value.FCast.Enabled;

    public async Task<IReadOnlyList<CastDeviceDto>> DiscoverAsync(bool refresh, CancellationToken cancellationToken)
    {
        var entries = await GetEntriesAsync(refresh, cancellationToken);
        return entries.Select(entry => entry.Device).ToArray();
    }

    public async Task<ICastSessionClient?> CreateClientAsync(string deviceId, CancellationToken cancellationToken)
    {
        var entries = await GetEntriesAsync(refresh: false, cancellationToken);
        var match = Find(entries, deviceId);
        if (match.Info is null)
        {
            entries = await GetEntriesAsync(refresh: true, cancellationToken);
            match = Find(entries, deviceId);
        }

        if (match.Info is null)
        {
            return null;
        }

        var context = new CastContext();
        var device = context.CreateDeviceFromInfo(match.Info);
        return new FCastSessionClient(context, device, loggerFactory.CreateLogger<FCastSessionClient>());
    }

    private Task<IReadOnlyList<(CastDeviceDto Device, DeviceInfo Info)>> GetEntriesAsync(
        bool refresh,
        CancellationToken cancellationToken)
        => _cache.GetAsync(refresh, ScanAsync, cancellationToken);

    private static (CastDeviceDto Device, DeviceInfo Info) Find(
        IReadOnlyList<(CastDeviceDto Device, DeviceInfo Info)> entries,
        string deviceId)
    {
        var localId = CastDeviceId.LocalIdOf(deviceId);
        return entries.FirstOrDefault(entry => entry.Device.Id == deviceId || CastDeviceId.LocalIdOf(entry.Device.Id) == localId);
    }

    private async Task<IReadOnlyList<(CastDeviceDto Device, DeviceInfo Info)>> ScanAsync(CancellationToken cancellationToken)
    {
        using var context = new CastContext();
        var handler = new DiscoveryHandler();
        context.StartDiscovery(handler);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.DiscoveryTimeoutSeconds));
        await Task.Delay(timeout, cancellationToken);

        var devices = handler.Devices
            .Where(info => info.protocol == ProtocolType.FCast)
            .Select(info => (ToDto(info), info))
            .ToArray();
        logger.LogDebug("FCast discovery found {Count} receiver(s).", devices.Length);
        return devices;
    }

    private static CastDeviceDto ToDto(DeviceInfo info)
    {
        var host = info.addresses.FirstOrDefault() is { } address ? AddressToString(address) : "";
        return new CastDeviceDto
        {
            Id = CastDeviceId.Create(CastProtocolIds.FCast, LocalIdOf(info)),
            Protocol = CastProtocolIds.FCast,
            Name = info.name,
            Host = host,
            Port = info.port,
            Model = "FCast",
            Status = info.protocol.ToString()
        };
    }

    private static string LocalIdOf(DeviceInfo info)
    {
        var identity = $"{info.name}|{info.port}|{string.Join(",", info.addresses.Select(AddressToString))}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static string AddressToString(IpAddr address)
        => address switch
        {
            IpAddr.V4 v4 => new IPAddress([v4.o1, v4.o2, v4.o3, v4.o4]).ToString(),
            IpAddr.V6 v6 => new IPAddress(
                [
                    v6.o1, v6.o2, v6.o3, v6.o4,
                    v6.o5, v6.o6, v6.o7, v6.o8,
                    v6.o9, v6.o10, v6.o11, v6.o12,
                    v6.o13, v6.o14, v6.o15, v6.o16
                ],
                v6.scopeId).ToString(),
            _ => ""
        };

    private sealed class DiscoveryHandler : DeviceDiscovererEventHandler
    {
        private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new(StringComparer.Ordinal);

        public IReadOnlyCollection<DeviceInfo> Devices => _devices.Values.ToArray();

        public void DeviceAvailable(DeviceInfo deviceInfo) => _devices[KeyOf(deviceInfo)] = deviceInfo;

        public void DeviceChanged(DeviceInfo deviceInfo) => _devices[KeyOf(deviceInfo)] = deviceInfo;

        public void DeviceRemoved(string deviceName)
        {
            foreach (var (key, device) in _devices)
            {
                if (string.Equals(device.name, deviceName, StringComparison.Ordinal))
                {
                    _devices.TryRemove(key, out _);
                }
            }
        }

        private static string KeyOf(DeviceInfo deviceInfo)
            => $"{deviceInfo.name}|{deviceInfo.port}|{string.Join(",", deviceInfo.addresses.Select(AddressToString))}";
    }
}

public sealed class FCastSessionClient : ICastSessionClient
{
    private readonly CastContext _context;
    private readonly CastingDevice _device;
    private readonly ILogger<FCastSessionClient> _logger;
    private readonly Lock _snapshotLock = new();
    private readonly FCastDeviceEventHandler _eventHandler;
    private TaskCompletionSource _connected = NewCompletionSource();

    private CastSessionSnapshot _snapshot = new()
    {
        PlayerState = "Loading",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private bool _hasMediaSession;

    public FCastSessionClient(CastContext context, CastingDevice device, ILogger<FCastSessionClient> logger)
    {
        _context = context;
        _device = device;
        _logger = logger;
        _eventHandler = new FCastDeviceEventHandler(this);
    }

    public event EventHandler<CastSessionSnapshot?>? StatusChanged;
    public event EventHandler? Disconnected;

    public bool HasMediaSession => _hasMediaSession;

    public async Task ConnectAsync()
    {
        _connected = NewCompletionSource();
        _device.Connect(appInfo: null, _eventHandler, reconnectIntervalMillis: 1000);
        await _connected.Task;
    }

    public Task<CastSessionSnapshot?> GetStatusAsync()
        => Task.FromResult(_hasMediaSession ? Snapshot() : null);

    public Task<CastSessionSnapshot?> LoadAsync(CastLoadSpec spec)
    {
        var metadata = new FcastMetadata(spec.Title, spec.ThumbnailUrl);
        _device.Load(new LoadRequest.Url(
            spec.ContentType,
            spec.ContentUrl,
            spec.StartPositionSeconds ?? 0,
            speed: null,
            volume: null,
            metadata,
            requestHeaders: null));

        _hasMediaSession = true;
        return Task.FromResult<CastSessionSnapshot?>(UpdateSnapshot(snapshot => snapshot with
        {
            PlayerState = "Buffering",
            CurrentTime = spec.StartPositionSeconds ?? 0,
            DurationSeconds = spec.DurationSeconds,
            UpdatedAt = DateTimeOffset.UtcNow
        }));
    }

    public Task<CastSessionSnapshot?> PlayAsync()
    {
        _device.ResumePlayback();
        return Task.FromResult<CastSessionSnapshot?>(UpdateSnapshot(snapshot => snapshot with
        {
            PlayerState = "Playing",
            UpdatedAt = DateTimeOffset.UtcNow
        }));
    }

    public Task<CastSessionSnapshot?> PauseAsync()
    {
        _device.PausePlayback();
        return Task.FromResult<CastSessionSnapshot?>(UpdateSnapshot(snapshot => snapshot with
        {
            PlayerState = "Paused",
            UpdatedAt = DateTimeOffset.UtcNow
        }));
    }

    public Task<CastSessionSnapshot?> StopAsync()
    {
        _device.StopPlayback();
        _hasMediaSession = false;
        return Task.FromResult<CastSessionSnapshot?>(UpdateSnapshot(snapshot => snapshot with
        {
            PlayerState = "Idle",
            UpdatedAt = DateTimeOffset.UtcNow
        }));
    }

    public Task<CastSessionSnapshot?> SeekAsync(double seconds)
    {
        _device.Seek(seconds);
        return Task.FromResult<CastSessionSnapshot?>(UpdateSnapshot(snapshot => snapshot with
        {
            CurrentTime = seconds,
            UpdatedAt = DateTimeOffset.UtcNow
        }));
    }

    public Task SetVolumeAsync(double level)
    {
        _device.ChangeVolume(level);
        UpdateSnapshot(snapshot => snapshot with
        {
            VolumeLevel = level,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted)
        => muted
            ? Task.FromException(new NotSupportedException("FCast does not expose mute control."))
            : Task.CompletedTask;

    public Task DisconnectAsync()
    {
        _device.Disconnect();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _device.Dispose();
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CastSessionSnapshot Snapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    private CastSessionSnapshot UpdateSnapshot(Func<CastSessionSnapshot, CastSessionSnapshot> update)
    {
        CastSessionSnapshot updated;
        lock (_snapshotLock)
        {
            updated = update(_snapshot);
            _snapshot = updated;
        }

        StatusChanged?.Invoke(this, updated);
        return updated;
    }

    private void OnConnectionStateChanged(DeviceConnectionState state)
    {
        switch (state)
        {
            case DeviceConnectionState.Connected:
                _connected.TrySetResult();
                break;
            case DeviceConnectionState.Disconnected:
                if (!_connected.Task.IsCompleted)
                {
                    _connected.TrySetException(new IOException("FCast receiver disconnected before connection completed."));
                    return;
                }

                Disconnected?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnVolumeChanged(double volume)
        => UpdateSnapshot(snapshot => snapshot with
        {
            VolumeLevel = volume,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    private void OnTimeChanged(double time)
        => UpdateSnapshot(snapshot => snapshot with
        {
            CurrentTime = time,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    private void OnPlaybackStateChanged(PlaybackState state)
    {
        _hasMediaSession = state != PlaybackState.Idle;
        UpdateSnapshot(snapshot => snapshot with
        {
            PlayerState = state.ToString(),
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private void OnDurationChanged(double duration)
        => UpdateSnapshot(snapshot => snapshot with
        {
            DurationSeconds = duration,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    private void OnPlaybackError(string message)
        => _logger.LogWarning("FCast receiver reported playback error: {Message}", message);

    private sealed class FCastDeviceEventHandler(FCastSessionClient owner) : DeviceEventHandler
    {
        public void ConnectionStateChanged(DeviceConnectionState state) => owner.OnConnectionStateChanged(state);

        public void VolumeChanged(double volume) => owner.OnVolumeChanged(volume);

        public void TimeChanged(double time) => owner.OnTimeChanged(time);

        public void PlaybackStateChanged(PlaybackState state) => owner.OnPlaybackStateChanged(state);

        public void DurationChanged(double duration) => owner.OnDurationChanged(duration);

        public void SpeedChanged(double speed) { }

        public void SourceChanged(Source source) { }

        public void KeyEvent(KeyEvent @event) { }

        public void MediaEvent(MediaEvent @event) { }

        public void PlaybackError(string message) => owner.OnPlaybackError(message);
    }
}
