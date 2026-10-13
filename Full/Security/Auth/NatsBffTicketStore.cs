using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using Shared.Messaging;

namespace WebAPI.Auth;

/// <summary>
/// Stores protected authentication tickets in NATS KV. The cookie contains only the random handle;
/// the KV key is a one-way hash so access to the bucket cannot be used to mint browser cookies.
/// </summary>
public sealed class NatsBffTicketStore(
    INatsKVContext kvContext,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<NatsBffTicketStore> logger) : ITicketStore
{
    private static readonly INatsSerializer<byte[]> Serializer = NatsDefaultSerializer<byte[]>.Default;
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "FrostStream", "BffAuthenticationTicket", "v1");
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private INatsKVStore? _store;

    public Task<string> StoreAsync(AuthenticationTicket ticket)
        => StoreAsync(ticket, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var handle = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(32));
        ticket.Properties.Items[BffAuthenticationDefaults.SessionKeyProperty] = handle;
        await PutTicketAsync(handle, ticket, cancellationToken);
        return handle;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
        => RenewAsync(key, ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        ticket.Properties.Items[BffAuthenticationDefaults.SessionKeyProperty] = key;
        return PutTicketAsync(key, ticket, cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
        => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await GetEntryAsync(SessionStorageKey(key), cancellationToken);
            if (entry is null)
            {
                return null;
            }

            var serialized = _protector.Unprotect(entry.Value);
            var ticket = TicketSerializer.Default.Deserialize(serialized);
            ticket?.Properties.Items.TryAdd(BffAuthenticationDefaults.SessionKeyProperty, key);
            return ticket;
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Discarding an unreadable BFF authentication ticket.");
            return null;
        }
        catch (Exception ex) when (ex is NatsKVException or JsonException)
        {
            logger.LogWarning(ex, "Failed retrieving a BFF authentication ticket from NATS KV.");
            return null;
        }
    }

    public Task RemoveAsync(string key)
        => RemoveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        var store = await GetStoreAsync(cancellationToken);
        await store.TryDeleteAsync(SessionStorageKey(key), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Acquires a short distributed lease in the same KV bucket. Stale leases are replaced with a
    /// revision-checked update, so a crashed WebAPI replica cannot strand refresh forever.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireRefreshLeaseAsync(string handle, CancellationToken cancellationToken)
    {
        var key = LeaseStorageKey(handle);
        var owner = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var store = await GetStoreAsync(cancellationToken);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var lease = new RefreshLease(owner, DateTimeOffset.UtcNow.AddSeconds(15));
            var payload = JsonSerializer.SerializeToUtf8Bytes(lease);
            try
            {
                await store.CreateAsync(key, payload, Serializer, cancellationToken);
                return new LeaseReleaser(this, key, owner);
            }
            catch (Exception createError)
            {
                var existing = await GetEntryAsync(key, cancellationToken);
                if (existing is null)
                {
                    throw new InvalidOperationException("NATS KV refresh lease could not be created.", createError);
                }

                var current = JsonSerializer.Deserialize<RefreshLease>(existing.Value);
                if (current is not null && current.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    try
                    {
                        await store.UpdateAsync(key, payload, existing.Revision, Serializer, cancellationToken);
                        return new LeaseReleaser(this, key, owner);
                    }
                    catch
                    {
                        // Another replica replaced the stale lease first. Wait and re-read.
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for the browser-session refresh lease.");
    }

    private async Task PutTicketAsync(string handle, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var serialized = TicketSerializer.Default.Serialize(ticket);
        var protectedTicket = _protector.Protect(serialized);
        var store = await GetStoreAsync(cancellationToken);
        await store.PutAsync(SessionStorageKey(handle), protectedTicket, Serializer, cancellationToken);
    }

    private async Task<StoredEntry?> GetEntryAsync(string key, CancellationToken cancellationToken)
    {
        var store = await GetStoreAsync(cancellationToken);
        var entry = await store.GetEntryAsync<byte[]>(
            key,
            serializer: Serializer,
            cancellationToken: cancellationToken);
        if (entry.Error is not null)
        {
            return null;
        }

        entry.EnsureSuccess();
        return entry.Value is null ? null : new StoredEntry(entry.Value, entry.Revision);
    }

    private async Task ReleaseLeaseAsync(string key, string owner)
    {
        try
        {
            var entry = await GetEntryAsync(key, CancellationToken.None);
            var lease = entry is null ? null : JsonSerializer.Deserialize<RefreshLease>(entry.Value);
            if (lease?.Owner != owner)
            {
                return;
            }

            var store = await GetStoreAsync(CancellationToken.None);
            await store.TryDeleteAsync(
                key,
                new NatsKVDeleteOpts { Revision = entry!.Revision },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed releasing a BFF token-refresh lease; it will expire and be replaced.");
        }
    }

    private async Task<INatsKVStore> GetStoreAsync(CancellationToken cancellationToken)
    {
        var store = Volatile.Read(ref _store);
        if (store is not null)
        {
            return store;
        }

        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            store = Volatile.Read(ref _store);
            if (store is null)
            {
                store = await kvContext.GetStoreAsync(
                    AuthSessionsTopology.BucketNameValue,
                    cancellationToken: cancellationToken);
                Volatile.Write(ref _store, store);
            }

            return store;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private static string SessionStorageKey(string handle) => "s_" + Hash(handle);

    private static string LeaseStorageKey(string handle) => "l_" + Hash(handle);

    private static string Hash(string handle)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));

    private sealed record RefreshLease(string Owner, DateTimeOffset ExpiresAt);

    private sealed record StoredEntry(byte[] Value, ulong Revision);

    private sealed class LeaseReleaser(NatsBffTicketStore store, string key, string owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await store.ReleaseLeaseAsync(key, owner);
    }
}
