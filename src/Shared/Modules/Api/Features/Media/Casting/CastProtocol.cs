namespace WebAPI.Features.Media.Casting;

/// <summary>
/// A cast transport (Chromecast, FCast, Roku). Each provider discovers its own devices and builds a
/// <see cref="ICastSessionClient"/> for one of them. Providers are aggregated by
/// <see cref="CastDeviceRegistry"/>.
/// </summary>
public interface ICastProtocol
{
    /// <summary>One of <see cref="CastProtocolIds"/>; also the prefix of every device id it owns.</summary>
    string Id { get; }

    /// <summary>Whether this protocol is turned on in configuration.</summary>
    bool Enabled { get; }

    /// <summary>Discovers devices, served from cache inside the configured TTL unless <paramref name="refresh"/>.</summary>
    Task<IReadOnlyList<CastDeviceDto>> DiscoverAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>
    /// Builds an unconnected client for the device, or null when the id is unknown even after a
    /// fresh scan. The caller wires events and calls <see cref="ICastSessionClient.ConnectAsync"/>.
    /// </summary>
    Task<ICastSessionClient?> CreateClientAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>An unconnected client plus the device it targets.</summary>
public sealed record CastSessionClientHandle(CastDeviceDto Device, ICastSessionClient Client);

public interface ICastDeviceRegistry
{
    Task<IReadOnlyList<CastDeviceDto>> GetDevicesAsync(bool refresh, CancellationToken cancellationToken);
    Task<CastSessionClientHandle?> CreateClientAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// Fans discovery across every enabled protocol and routes a session request to the protocol that
/// owns the device id (by its <c>{protocol}:</c> prefix). Discovery failures in one protocol never
/// hide the devices of another.
/// </summary>
public sealed class CastDeviceRegistry(IEnumerable<ICastProtocol> protocols, ILogger<CastDeviceRegistry> logger)
    : ICastDeviceRegistry
{
    private readonly IReadOnlyList<ICastProtocol> _protocols = protocols.Where(protocol => protocol.Enabled).ToArray();

    public async Task<IReadOnlyList<CastDeviceDto>> GetDevicesAsync(bool refresh, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(_protocols.Select(protocol => DiscoverSafelyAsync(protocol, refresh, cancellationToken)));
        return results
            .SelectMany(devices => devices)
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CastSessionClientHandle?> CreateClientAsync(string deviceId, CancellationToken cancellationToken)
    {
        var protocolId = CastDeviceId.ProtocolOf(deviceId);
        var protocol = _protocols.FirstOrDefault(candidate => candidate.Id == protocolId);
        if (protocol is null)
        {
            return null;
        }

        var client = await protocol.CreateClientAsync(deviceId, cancellationToken);
        if (client is null)
        {
            return null;
        }

        var localId = CastDeviceId.LocalIdOf(deviceId);
        var device = (await DiscoverSafelyAsync(protocol, refresh: false, cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == deviceId || CastDeviceId.LocalIdOf(candidate.Id) == localId);
        if (device is null)
        {
            await client.DisposeAsync();
            return null;
        }

        return new CastSessionClientHandle(device, client);
    }

    private async Task<IReadOnlyList<CastDeviceDto>> DiscoverSafelyAsync(
        ICastProtocol protocol,
        bool refresh,
        CancellationToken cancellationToken)
    {
        try
        {
            return await protocol.DiscoverAsync(refresh, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cast discovery failed for protocol {Protocol}.", protocol.Id);
            return [];
        }
    }
}

/// <summary>
/// Shared TTL cache for protocols that discover by active scanning. Serializes
/// scans behind a semaphore so concurrent refreshes don't stampede the network, and a waiter that
/// lost the race reuses the scan the winner just completed.
/// </summary>
public sealed class CastDiscoveryCache<T>(TimeSpan ttl)
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private IReadOnlyList<T> _cache = [];
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<T>> GetAsync(
        bool refresh,
        Func<CancellationToken, Task<IReadOnlyList<T>>> scan,
        CancellationToken cancellationToken)
    {
        if (!refresh && DateTimeOffset.UtcNow < _expiresAt)
        {
            return _cache;
        }

        var staleExpiry = _expiresAt;
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            if (_expiresAt > staleExpiry && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _cache;
            }

            _cache = await scan(cancellationToken);
            _expiresAt = DateTimeOffset.UtcNow.Add(ttl);
            return _cache;
        }
        finally
        {
            _scanLock.Release();
        }
    }
}
