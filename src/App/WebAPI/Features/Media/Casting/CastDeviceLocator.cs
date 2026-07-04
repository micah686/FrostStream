using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sharpcaster;
using Sharpcaster.Models;

namespace WebAPI.Features.Media.Casting;

public interface ICastDeviceLocator
{
    /// <summary>Lists devices, served from cache inside the configured TTL unless <paramref name="refresh"/>.</summary>
    Task<IReadOnlyList<CastDeviceDto>> GetDevicesAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>Resolves a device id to its receiver, rescanning once if it is not in the cache.</summary>
    Task<ChromecastReceiver?> FindReceiverAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// mDNS discovery via Sharpcaster's <see cref="ChromecastLocator"/> with a TTL cache. Scans are
/// serialized behind a semaphore so concurrent refresh requests don't stampede the network, and a
/// waiter that lost the race reuses the scan the winner just completed.
/// </summary>
public sealed class SharpcasterDeviceLocator(
    IOptions<CastingOptions> options,
    ILogger<SharpcasterDeviceLocator> logger) : ICastDeviceLocator
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private IReadOnlyList<(CastDeviceDto Device, ChromecastReceiver Receiver)> _cache = [];
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<CastDeviceDto>> GetDevicesAsync(bool refresh, CancellationToken cancellationToken)
    {
        var entries = await GetEntriesAsync(refresh, cancellationToken);
        return entries.Select(entry => entry.Device).ToArray();
    }

    public async Task<ChromecastReceiver?> FindReceiverAsync(string deviceId, CancellationToken cancellationToken)
    {
        var entries = await GetEntriesAsync(refresh: false, cancellationToken);
        var match = entries.FirstOrDefault(entry => entry.Device.Id == deviceId);
        if (match.Receiver is not null)
        {
            return match.Receiver;
        }

        // Not in the cache — the device may have joined the network since the last scan.
        entries = await GetEntriesAsync(refresh: true, cancellationToken);
        return entries.FirstOrDefault(entry => entry.Device.Id == deviceId).Receiver;
    }

    private async Task<IReadOnlyList<(CastDeviceDto Device, ChromecastReceiver Receiver)>> GetEntriesAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (!refresh && DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            return _cache;
        }

        var staleExpiry = _cacheExpiresAt;
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have finished a scan while we waited; their result is fresh enough.
            if (_cacheExpiresAt > staleExpiry && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cache;
            }

            var receivers = await ScanAsync();
            _cache = receivers
                .Where(receiver => receiver.DeviceUri is not null)
                .Select(receiver => (ToDto(receiver), receiver))
                .ToArray();
            _cacheExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.Value.DeviceCacheSeconds));
            return _cache;
        }
        finally
        {
            _scanLock.Release();
        }
    }

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
        if (receiver.ExtraInformation is not null &&
            receiver.ExtraInformation.TryGetValue("id", out var id) &&
            !string.IsNullOrWhiteSpace(id))
        {
            return id.Replace("-", "").ToLowerInvariant();
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{receiver.DeviceUri.Host}:{receiver.Port}"));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
