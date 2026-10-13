using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sharpcaster;
using Sharpcaster.Models;

namespace WebAPI.Features.Media.Casting;

/// <summary>
/// mDNS discovery via Sharpcaster's <see cref="ChromecastLocator"/> with a TTL cache. Scans are
/// serialized by <see cref="CastDiscoveryCache{T}"/> so concurrent refresh requests don't
/// stampede the network.
/// </summary>
public sealed class ChromecastCastProtocol(
    IOptions<CastingOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<ChromecastCastProtocol> logger) : ICastProtocol
{
    private readonly CastDiscoveryCache<(CastDeviceDto Device, ChromecastReceiver Receiver)> _cache =
        new(TimeSpan.FromSeconds(Math.Max(1, options.Value.DeviceCacheSeconds)));

    public string Id => CastProtocolIds.Chromecast;

    public bool Enabled => options.Value.Chromecast.Enabled;

    public async Task<IReadOnlyList<CastDeviceDto>> DiscoverAsync(bool refresh, CancellationToken cancellationToken)
    {
        var entries = await GetEntriesAsync(refresh, cancellationToken);
        return entries.Select(entry => entry.Device).ToArray();
    }

    public async Task<ICastSessionClient?> CreateClientAsync(string deviceId, CancellationToken cancellationToken)
    {
        var entries = await GetEntriesAsync(refresh: false, cancellationToken);
        var localId = CastDeviceId.LocalIdOf(deviceId);
        var match = entries.FirstOrDefault(entry => entry.Device.Id == deviceId || CastDeviceId.LocalIdOf(entry.Device.Id) == localId);
        if (match.Receiver is not null)
        {
            return CreateClient(match.Receiver);
        }

        // Not in the cache — the device may have joined the network since the last scan.
        entries = await GetEntriesAsync(refresh: true, cancellationToken);
        match = entries.FirstOrDefault(entry => entry.Device.Id == deviceId || CastDeviceId.LocalIdOf(entry.Device.Id) == localId);
        return match.Receiver is null ? null : CreateClient(match.Receiver);
    }

    private Task<IReadOnlyList<(CastDeviceDto Device, ChromecastReceiver Receiver)>> GetEntriesAsync(
        bool refresh,
        CancellationToken cancellationToken)
        => _cache.GetAsync(
            refresh,
            async _ =>
            {
                var receivers = await ScanAsync();
                return receivers
                    .Where(receiver => receiver.DeviceUri is not null)
                    .Select(receiver => (ToDto(receiver), receiver))
                    .ToArray();
            },
            cancellationToken);

    private SharpcasterSessionClient CreateClient(ChromecastReceiver receiver)
        => new(
            receiver,
            loggerFactory.CreateLogger<ChromecastClient>(),
            loggerFactory.CreateLogger<SharpcasterSessionClient>());

    private async Task<IEnumerable<ChromecastReceiver>> ScanAsync()
    {
        using var locator = new ChromecastLocator();
        var fullTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.DiscoveryTimeoutSeconds));
        var receivers = await locator.FindReceiversAsync(fullTimeout: fullTimeout);
        var found = receivers.ToArray();
        logger.LogDebug("mDNS discovery found {Count} cast device(s).", found.Length);
        return found;
    }

    private static CastDeviceDto ToDto(ChromecastReceiver receiver)
        => new()
        {
            Id = DeviceIdOf(receiver),
            Protocol = CastProtocolIds.Chromecast,
            Name = receiver.Name,
            Host = receiver.DeviceUri.Host,
            Port = receiver.Port,
            Model = receiver.Model,
            Version = receiver.Version,
            Status = receiver.Status
        };

    /// <summary>
    /// Stable device id: the mDNS TXT <c>id</c> record (the device's own UUID) when present,
    /// otherwise a hash of host:port. Hex-only so it is safe in route segments.
    /// </summary>
    internal static string DeviceIdOf(ChromecastReceiver receiver)
    {
        string localId;
        if (receiver.ExtraInformation is not null &&
            receiver.ExtraInformation.TryGetValue("id", out var id) &&
            !string.IsNullOrWhiteSpace(id))
        {
            localId = id.Replace("-", "").ToLowerInvariant();
        }
        else
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{receiver.DeviceUri.Host}:{receiver.Port}"));
            localId = Convert.ToHexStringLower(bytes)[..16];
        }

        return CastDeviceId.Create(CastProtocolIds.Chromecast, localId);
    }
}
