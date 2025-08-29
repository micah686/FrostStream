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
        string FriendlyName,     // e.g., "worker-abc", "WebApi", "DataBridge"
        ServiceType Type,        // Logical service type
        byte[]? Identity,        // ROUTER identity bytes (nullable)
        DateTime LastSeenUtc     // Last heartbeat or message seen
    );

    /// <summary>
    /// In-memory, thread-safe registry for currently-connected services.
    /// Single authoritative map keyed by friendly name (string).
    ///
    /// Intended usage:
    ///   - Upsert when you see a service register or heartbeat.
    ///   - Touch to bump LastSeenUtc without changing identity/type.
    ///   - UpdateIdentity if identity becomes known/changes.
    ///   - GetByType for routing/broadcast by type.
    ///
    /// Notes:
    ///   - Assumes friendly names are unique. If you allow duplicate friendly
    ///     names across instances, consider composing a stable suffix (e.g.,
    ///     worker-abc#1) at registration time or maintain a secondary index.
    /// </summary>
    public sealed class ServiceRegistry
    {
        // Authoritative collection keyed by friendly name
        private readonly ConcurrentDictionary<string, ServiceRecord> _byName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Insert or refresh a service entry.
        /// - If an entry with the same friendly name exists, updates Type/Identity
        ///   (if provided) and LastSeenUtc.
        /// - If identity is null, preserves the existing Identity.
        /// </summary>
        public ServiceRecord Upsert(
            string friendlyName,
            ServiceType type,
            byte[]? identityBytes = null)
        {
            var now = DateTime.UtcNow;

            return _byName.AddOrUpdate(
                key: friendlyName,
                addValueFactory: _ =>
                    new ServiceRecord(friendlyName, type, identityBytes, now),
                updateValueFactory: (_, existing) =>
                    existing with
                    {
                        Type = type,
                        Identity = identityBytes ?? existing.Identity,
                        LastSeenUtc = now
                    });
        }

        /// <summary>
        /// Upsert overload that accepts a ReadOnlySpan&lt;byte&gt; for identity.
        /// Pass an empty span (or call Touch) if you don't want to change identity.
        /// </summary>
        public ServiceRecord Upsert(
            string friendlyName,
            ServiceType type,
            ReadOnlySpan<byte> identity)
        {
            // Copy to array to store in record (records should not hold stack spans)
            var idCopy = identity.Length == 0 ? null : identity.ToArray();
            return Upsert(friendlyName, type, idCopy);
        }

        /// <summary>
        /// Bump LastSeenUtc for a service. If no record exists, creates a placeholder
        /// with Type=None and Identity=null so callers can Touch before full registration.
        /// </summary>
        public void Touch(string friendlyName)
        {
            var now = DateTime.UtcNow;

            _byName.AddOrUpdate(
                friendlyName,
                addValueFactory: _ => new ServiceRecord(friendlyName, ServiceType.None, null, now),
                updateValueFactory: (_, existing) => existing with { LastSeenUtc = now });
        }

        /// <summary>
        /// Update (or set) the ROUTER identity bytes for a service without changing other fields.
        /// Returns false if the service does not exist.
        /// </summary>
        public bool UpdateIdentity(string friendlyName, byte[]? identityBytes)
        {
            if (!_byName.TryGetValue(friendlyName, out var existing))
                return false;

            var now = DateTime.UtcNow;
            _byName[friendlyName] = existing with { Identity = identityBytes, LastSeenUtc = now };
            return true;
        }

        /// <summary>
        /// Convenience overload for identity coming in as a span.
        /// </summary>
        public bool UpdateIdentity(string friendlyName, ReadOnlySpan<byte> identity)
        {
            return UpdateIdentity(friendlyName, identity.Length == 0 ? null : identity.ToArray());
        }

        /// <summary>Remove a service (e.g., on disconnect or prune).</summary>
        public bool Remove(string friendlyName) => _byName.TryRemove(friendlyName, out _);

        /// <summary>Get a snapshot of all currently-known services.</summary>
        public IReadOnlyList<ServiceRecord> GetAll() => _byName.Values.ToList();

        /// <summary>Try get by friendly name.</summary>
        public bool TryGet(string friendlyName, out ServiceRecord record) =>
            _byName.TryGetValue(friendlyName, out record);

        /// <summary>Return snapshot of services for a given type.</summary>
        public IReadOnlyList<ServiceRecord> GetByType(ServiceType type) =>
            _byName.Values.Where(r => r.Type == type).ToList();

        /// <summary>
        /// Resolve a friendly name to its identity (if known).
        /// Returns false if the service isn't present or doesn't have an identity yet.
        /// </summary>
        public bool TryGetIdentity(string friendlyName, out byte[] identityBytes)
        {
            if (_byName.TryGetValue(friendlyName, out var rec) && rec.Identity is { Length: > 0 } id)
            {
                identityBytes = id;
                return true;
            }
            identityBytes = Array.Empty<byte>();
            return false;
        }

        /// <summary>
        /// Prune any service not seen since (UtcNow - ttl).
        /// Returns the number of removed records.
        /// </summary>
        public int PruneStale(TimeSpan ttl)
        {
            var cutoff = DateTime.UtcNow - ttl;
            var removed = 0;

            foreach (var kvp in _byName)
            {
                if (kvp.Value.LastSeenUtc < cutoff && _byName.TryRemove(kvp.Key, out _))
                    removed++;
            }
            return removed;
        }

        /// <summary>Count (for metrics/tests).</summary>
        public int Count => _byName.Count;
    }
}
