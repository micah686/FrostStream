using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.DataBridge;

internal sealed class TransferLeaseManager : IDisposable
{
    private readonly int _maxLeases = Math.Min(Environment.ProcessorCount / 2, 4);
    private readonly TimeSpan _leaseTtl = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _inactivity = TimeSpan.FromSeconds(120);
    private readonly ConcurrentDictionary<Guid, LeaseInfo> _leases = new();
    private readonly CancellationTokenSource _cts = new();

    public TransferLeaseManager()
    {
        _ = Task.Run(LeaseMonitorLoop);
    }

    public void StartBrokerLoop()
    {
        using var socket = new DealerSocket(">tcp://localhost:5557");
        while (!_cts.IsCancellationRequested)
        {
            var msg = socket.ReceiveMultipartMessage();
            var wire = WireMessage.FromNetMQMessage(msg);
            if (wire.Command == ControlCommand.TransferLeaseRequest)
            {
                var workerId = Guid.TryParse(wire.WorkerId, out var id) ? id : Guid.Empty;
                if (TryGrantLease(workerId, out var leaseId))
                {
                    var granted = new WireMessage(ControlCommand.TransferGranted, Guid.Empty, workerId.ToString(), correlationId: leaseId);
                    socket.SendMultipartMessage(granted.ToNetMQMessage());
                }
                else
                {
                    var denied = new WireMessage(ControlCommand.TransferDenied, Guid.Empty, wire.WorkerId);
                    socket.SendMultipartMessage(denied.ToNetMQMessage());
                }
            }
        }
    }

    private bool TryGrantLease(Guid workerId, out Guid leaseId)
    {
        leaseId = Guid.Empty;
        if (workerId == Guid.Empty) return false;
        if (_leases.ContainsKey(workerId)) return false;
        if (_leases.Count >= _maxLeases) return false;

        leaseId = Guid.NewGuid();
        _leases[workerId] = new LeaseInfo { LeaseId = leaseId, GrantedAt = DateTime.UtcNow, LastActivity = DateTime.UtcNow };
        return true;
    }

    public void UpdateActivity(Guid workerId)
    {
        if (_leases.TryGetValue(workerId, out var info))
        {
            info.LastActivity = DateTime.UtcNow;
        }
    }

    public void ReleaseLease(Guid workerId)
    {
        _leases.TryRemove(workerId, out _);
    }

    private async Task LeaseMonitorLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _leases.ToArray())
            {
                if (now - kv.Value.GrantedAt > _leaseTtl || now - kv.Value.LastActivity > _inactivity)
                {
                    _leases.TryRemove(kv.Key, out _);
                }
            }
            await Task.Delay(1000, _cts.Token);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
    }

    private class LeaseInfo
    {
        public Guid LeaseId { get; set; }
        public DateTime GrantedAt { get; set; }
        public DateTime LastActivity { get; set; }
    }
}
