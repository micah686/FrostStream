using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using FrostStream.Shared;
using Microsoft.Extensions.Hosting;

namespace FrostStream.MessageHub.New
{
    public sealed record ServiceRecord(
        string IdentityKey,     // Base64 of ROUTER identity
        ServiceType Type,
        string Name,            // Friendly/logical name (e.g., worker instance name)
        DateTime LastSeenUtc
    );

    /// <summary>
    /// In-memory, thread-safe registry for currently-connected services.
    /// Single authoritative map keyed by ROUTER identity (Base64).
    /// </summary>
    public sealed class ServiceRegistry
    {
        // One authoritative collection
        private readonly ConcurrentDictionary<string, ServiceRecord> _byId = new();

        public static string IdentityKey(ReadOnlySpan<byte> identityBytes)
            => Convert.ToBase64String(identityBytes);

        /// <summary>Insert or refresh presence for a service.</summary>
        public ServiceRecord Upsert(string identityKey, ServiceType type, string name, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            return _byId.AddOrUpdate(
                identityKey,
                addValueFactory: _ => new ServiceRecord(identityKey, type, name, now),
                updateValueFactory: (_, existing) =>
                    existing with { Type = type, Name = name, LastSeenUtc = now });
        }

        /// <summary>Touch last-seen for an identity. No-ops if not present.</summary>
        public void Touch(string identityKey, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            _byId.AddOrUpdate(identityKey,
                _ => new ServiceRecord(identityKey, ServiceType.None, "unknown", now),
                (_, existing) => existing with { LastSeenUtc = now });
        }

        /// <summary>Remove an identity (e.g., on disconnect or prune).</summary>
        public bool Remove(string identityKey) => _byId.TryRemove(identityKey, out _);

        /// <summary>Get a snapshot of all currently-known services.</summary>
        public IReadOnlyList<ServiceRecord> GetAll() => _byId.Values.ToList();

        /// <summary>Try get by identity.</summary>
        public bool TryGet(string identityKey, out ServiceRecord record) => _byId.TryGetValue(identityKey, out record);

        /// <summary>Return snapshot of identities for a given type.</summary>
        public IReadOnlyList<ServiceRecord> GetByType(ServiceType type)
            => _byId.Values.Where(r => r.Type == type).ToList();

        /// <summary>Resolve a logical name within a type to an identity (useful for sticky worker routing).</summary>
        public bool TryResolve(ServiceType type, string name, out ServiceRecord record)
        {
            // Small set; single broker; a LINQ scan is fine.
            // If you ever have 10k+ services, you can add a small secondary index.
            var match = _byId.Values.FirstOrDefault(r => r.Type == type &&
                                                         string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                record = default!;
                return false;
            }
            record = match;
            return true;
        }

        /// <summary>Prune anything not seen since (UtcNow - ttl).</summary>
        public int PruneStale(TimeSpan ttl)
        {
            var cutoff = DateTime.UtcNow - ttl;
            var removed = 0;
            foreach (var kv in _byId)
            {
                if (kv.Value.LastSeenUtc < cutoff && _byId.TryRemove(kv.Key, out _))
                    removed++;
            }
            return removed;
        }

        /// <summary>Count (for metrics/tests).</summary>
        public int Count => _byId.Count;
    }
}
