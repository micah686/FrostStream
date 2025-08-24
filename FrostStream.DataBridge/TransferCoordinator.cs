using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FrostStream.Shared;

namespace FrostStream.DataBridge;

/// <summary>
/// Coordinates file‑transfer slots for workers.  Uses a semaphore to limit
/// concurrent transfers and issues lease tokens to enforce hard back‑pressure.
/// </summary>
public sealed class TransferCoordinator
{
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, int> _byWorker = new(); // workerId -> count
    private readonly ConcurrentDictionary<Guid, Lease> _leases = new();   // LeaseId -> Lease
    private readonly Random _rng = new();
    private readonly int _maxConcurrentTransfers;
    private readonly int _perWorkerLimit = 1;
    private readonly TimeSpan _leaseTtl = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _transferInactivityTimeout = TimeSpan.FromSeconds(120);
    private readonly long _progressEveryBytes = 10 * 1024 * 1024; // 10 MB
    private readonly (int Min, int Max) _retryAfterRange = (10, 30);

    public TransferCoordinator(int? maxConcurrent = null)
    {
        _maxConcurrentTransfers = maxConcurrent ?? Math.Min(Environment.ProcessorCount / 2, 4);
        if (_maxConcurrentTransfers < 1) _maxConcurrentTransfers = 1;
        _slots = new SemaphoreSlim(_maxConcurrentTransfers);
    }

    /// <summary>
    /// Attempts to reserve a transfer slot for the given worker and job.  Returns a lease or a deny response.
    /// </summary>
    public TransferReply Reserve(Guid jobId, string workerId, long sizeBytes)
    {
        // enforce per‑worker limit
        if (_byWorker.GetOrAdd(workerId, _ => 0) >= _perWorkerLimit)
            return new TransferDenied(jobId, RetryAfter());

        // deny if all slots are in use
        if (!_slots.Wait(0))
            return new TransferDenied(jobId, RetryAfter());

        var lease = new Lease
        {
            LeaseId = Guid.NewGuid(),
            JobId = jobId,
            WorkerId = workerId,
            ExpiresAtUtc = DateTime.UtcNow + _leaseTtl,
            LastActivityUtc = DateTime.UtcNow
        };

        _leases[lease.LeaseId] = lease;
        _byWorker[workerId]++;

        return new TransferGranted(jobId, lease.LeaseId, lease.ExpiresAtUtc, 9000);
    }

    /// <summary>
    /// Called by workers when they begin streaming.  Verifies that the lease is valid and belongs to them.
    /// </summary>
    public bool TryBegin(Guid leaseId, string workerId)
    {
        if (!_leases.TryGetValue(leaseId, out var lease)) return false;
        if (lease.WorkerId != workerId) return false;
        if (lease.ExpiresAtUtc < DateTime.UtcNow) { Cancel(leaseId); return false; }

        lease.MarkStreamingStarted();
        return true;
    }

    /// <summary>
    /// Updates activity on a lease, extending its last‑activity timestamp.
    /// </summary>
    public void UpdateActivity(Guid leaseId)
    {
        if (_leases.TryGetValue(leaseId, out var lease))
            lease.LastActivityUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Completes the lease and frees the slot.  Success/failure is recorded for future use.
    /// </summary>
    public void Complete(Guid leaseId, bool success)
    {
        if (_leases.TryRemove(leaseId, out var lease))
        {
            _byWorker.AddOrUpdate(lease.WorkerId, 0, (_, v) => Math.Max(0, v - 1));
            _slots.Release();
        }
    }

    /// <summary>
    /// Cancels a lease and frees the slot.
    /// </summary>
    public void Cancel(Guid leaseId)
    {
        if (_leases.TryRemove(leaseId, out var lease))
        {
            _byWorker.AddOrUpdate(lease.WorkerId, 0, (_, v) => Math.Max(0, v - 1));
            _slots.Release();
        }
    }

    /// <summary>
    /// Scans for and cleans up expired or inactive leases.
    /// </summary>
    public void CleanupExpiredLeases()
    {
        foreach (var lease in _leases.Values.ToList())
        {
            if (DateTime.UtcNow - lease.LastActivityUtc > _transferInactivityTimeout ||
                lease.ExpiresAtUtc < DateTime.UtcNow)
            {
                Cancel(lease.LeaseId);
            }
        }
    }

    private int RetryAfter() => _rng.Next(_retryAfterRange.Min, _retryAfterRange.Max + 1);

    private sealed class Lease
    {
        public Guid LeaseId { get; init; }
        public Guid JobId { get; init; }
        public string WorkerId { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public DateTime LastActivityUtc { get; set; }
        public bool StreamingStarted { get; private set; }
        public void MarkStreamingStarted() => StreamingStarted = true;
    }
}
