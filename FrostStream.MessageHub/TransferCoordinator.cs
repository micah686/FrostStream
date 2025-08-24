using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.MessageHub;

public sealed class TransferCoordinator
{
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, int> _byWorker = new(); // workerId -> count
    private readonly ConcurrentDictionary<Guid, Lease> _leases = new();   // LeaseId -> Lease
    private readonly Random _rng = new();

    // 🔹 Parameters (hard defaults from design doc)
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

    public TransferReply Reserve(Guid jobId, string workerId, long sizeBytes)
    {
        if (_byWorker.GetOrAdd(workerId, _ => 0) >= _perWorkerLimit)
            return TransferReply.Denied(jobId, RetryAfter());

        if (!_slots.Wait(0))
            return TransferReply.Denied(jobId, RetryAfter());

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

        return TransferReply.Granted(jobId, lease.LeaseId, lease.ExpiresAtUtc, port: 9000);
    }

    public bool TryBegin(Guid leaseId, string workerId)
    {
        if (!_leases.TryGetValue(leaseId, out var lease)) return false;
        if (lease.WorkerId != workerId) return false;
        if (lease.ExpiresAtUtc < DateTime.UtcNow) { Cancel(leaseId); return false; }

        lease.MarkStreamingStarted();
        return true;
    }

    public void UpdateActivity(Guid leaseId)
    {
        if (_leases.TryGetValue(leaseId, out var lease))
            lease.LastActivityUtc = DateTime.UtcNow;
    }

    public void Complete(Guid leaseId, bool success)
    {
        if (_leases.TryRemove(leaseId, out var lease))
        {
            _byWorker.AddOrUpdate(lease.WorkerId, 0, (_, v) => Math.Max(0, v - 1));
            _slots.Release();
        }
    }

    public void Cancel(Guid leaseId)
    {
        if (_leases.TryRemove(leaseId, out var lease))
        {
            _byWorker.AddOrUpdate(lease.WorkerId, 0, (_, v) => Math.Max(0, v - 1));
            _slots.Release();
        }
    }

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

public abstract record TransferReply(Guid JobId)
{
    public static TransferReply Granted(Guid jobId, Guid leaseId, DateTime expiresAtUtc, int port)
        => new TransferGranted(jobId, leaseId, expiresAtUtc, port);

    public static TransferReply Denied(Guid jobId, int retryAfterSec)
        => new TransferDenied(jobId, retryAfterSec);
}

public sealed record TransferGranted(Guid JobId, Guid LeaseId, DateTime ExpiresAtUtc, int Port) : TransferReply(JobId);
public sealed record TransferDenied(Guid JobId, int RetryAfterSeconds) : TransferReply(JobId);

public record TransferRequest(Guid JobId, string WorkerId, long SizeBytes);
