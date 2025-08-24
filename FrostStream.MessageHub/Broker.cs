using NetMQ;
using NetMQ.Sockets;
using FrostStream.Shared;

namespace FrostStream.MessageHub;

public class Broker: IDisposable
{
    private readonly string _dbPath = "jobs.db";
    private readonly JobScheduler _scheduler;
    private readonly CancellationTokenSource _cts = new();
    
    private NetMQPoller _poller;
    private RouterSocket _workers;
    private RouterSocket _webapi;
    private RouterSocket _databridge;

    private byte[]? _webApiIdentity;
    private byte[]? _databridgeIdentity;

    public Broker()
    {
        _scheduler = new JobScheduler(_dbPath);
    }

    public void Start()
    {
        _workers = new RouterSocket("@tcp://*:5555");   // Workers
        _webapi  = new RouterSocket("@tcp://*:5556");   // WebAPI (one only)
        _databridge = new RouterSocket("@tcp://*:5557"); // DataBridge (one only)
        _poller = new NetMQPoller { _workers, _webapi, _databridge };

        _workers.ReceiveReady += (s, e) => HandleMessage(e.Socket, _workers, _webapi, _databridge);
        _webapi.ReceiveReady  += (s, e) => HandleMessage(e.Socket, _workers, _webapi, _databridge);
        _databridge.ReceiveReady += (s, e) => HandleMessage(e.Socket, _workers, _webapi, _databridge);


        // Start heartbeat monitor in background
        var hbThread = new Thread(() => HeartbeatLoop(_workers)) { IsBackground = true };
        hbThread.Start();

        Console.WriteLine("MessageBroker starting...");

        // Try to requeue pending jobs stored from previous run.
        // (This will only dispatch to currently-known idle workers — if none are present,
        //  jobs remain Pending until workers connect.)
        _scheduler.RequeueJobs(_workers);

        Console.WriteLine("MessageBroker running...");
        _poller.Run();
    }
    
    public void Stop()
    {
        Console.WriteLine("Stopping broker...");
        _cts.Cancel();
        _poller?.Stop();
        _workers?.Dispose();
        _webapi?.Dispose();
        _databridge?.Dispose();
    }
    
    public void Dispose() => Stop();

    private void HandleMessage(NetMQSocket sender, RouterSocket workers, RouterSocket webapi, RouterSocket databridge)
    {
        var msg = sender.ReceiveMultipartMessage();
        var wire = WireMessage.FromNetMQMessage(msg);

        // First frame = sender identity
        var senderIdentity = msg[0].ToByteArray();

        switch (wire.Command)
        {
            case ControlCommand.Heartbeat:
                WorkerComms(wire, workers, senderIdentity);
                break;
            case ControlCommand.Ready:
                WorkerComms(wire, workers, senderIdentity);
                break;

            case ControlCommand.ProgressUpdate:
                WorkerComms(wire, workers, senderIdentity);
                break;
            case ControlCommand.JobDone:
                WorkerComms(wire, workers, senderIdentity);
                break;
            case ControlCommand.JobDispatch:
                WorkerComms(wire, workers, senderIdentity);
                break;
            case ControlCommand.CancelJob:
                WorkerComms(wire, workers, senderIdentity);
                break;

            // ---- Worker -> DataBridge ----
            case ControlCommand.TransferReserve:
                if (_databridgeIdentity != null)
                    SendTo(databridge, _databridgeIdentity, wire);
                break;
            case ControlCommand.PayloadToDataBridge:
                if (_databridgeIdentity != null)
                    SendTo(databridge, _databridgeIdentity, wire);
                break;

            // ---- DataBridge -> Worker ----
            case ControlCommand.TransferGranted:
            case ControlCommand.TransferDenied:
            case ControlCommand.PayloadAck:
            case ControlCommand.PayloadNack:
                if (wire.WorkerId != null && _scheduler.TryGetWorkerIdentity(wire.WorkerId, out var workerId))
                    SendTo(workers, workerId, wire);
                break;

            // ---- Service-level (WebAPI <-> Broker) ----
            case ControlCommand.ServiceRequest:
                _webApiIdentity = senderIdentity;
                break;

            case ControlCommand.ServiceReply:
                if (_webApiIdentity != null)
                    SendTo(webapi, _webApiIdentity, wire);
                break;

            default:
                Console.WriteLine($"Unknown command: {wire.Command}");
                break;
        }

        // Track WebAPI/DataBridge identities automatically
        if (sender == webapi) _webApiIdentity = senderIdentity;
        if (sender == databridge) _databridgeIdentity = senderIdentity;
    }

    private void WorkerComms(WireMessage wire, RouterSocket workers, byte[] senderIdentity)
    {
        var cmd = wire.Command;
        if (cmd == ControlCommand.Heartbeat)
        {
            _scheduler.HeartbeatOrRegister(senderIdentity, wire.WorkerId);
        }
        else if (cmd == ControlCommand.Ready)
        {
            _scheduler.RegisterWorker(senderIdentity, wire.WorkerId);
            //_scheduler.RegisterWorker(senderIdentity, wire.WorkerId);
            // Send ACK so worker knows it's registered
            var ack = new WireMessage(ControlCommand.ReadyAck, Guid.Empty, wire.WorkerId);
            SendTo(workers, senderIdentity, ack);
            _scheduler.RequeueJobs(workers);
        }
        if (cmd == ControlCommand.JobDone)
        {
            _scheduler.HeartbeatOrRegister(senderIdentity, wire.WorkerId);
            _scheduler.MarkJobDone(wire.JobId, wire.WorkerId);
            if (_webApiIdentity != null)
                SendTo(_webapi, _webApiIdentity, wire);
        }
        else if (cmd == ControlCommand.ProgressUpdate)
        {
            _scheduler.HeartbeatOrRegister(senderIdentity, wire.WorkerId);
            if (_webApiIdentity != null)
                SendTo(_webapi, _webApiIdentity, wire);
        }
        else if (cmd == ControlCommand.JobDispatch)
        {
            _scheduler.AssignJob(wire, workers);
        }
        else if (cmd == ControlCommand.CancelJob)
        {
            _scheduler.CancelJob(wire.JobId);
        }
    }

    private void SendTo(RouterSocket socket, byte[] destinationIdentity, WireMessage wire)
    {
        var outMsg = wire.ToNetMQMessage(destinationIdentity);
        socket.SendMultipartMessage(outMsg);
    }

    private void HeartbeatLoop(RouterSocket workers)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _scheduler.CheckHeartbeats(workers);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeartbeatLoop] Error: {ex.Message}");
            }
            Thread.Sleep(TimeSpan.FromSeconds(10));
        }
    }
}
