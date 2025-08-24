using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;
using FrostStream.Worker.DataTransfer;

namespace FrostStream.Worker;

internal class Worker
{
    private readonly string _workerId;
    private readonly CancellationTokenSource _cts = new();

    public Worker(string workerId)
    {
        _workerId = workerId;
    }

    public void Run()
    {
        using var socket = new DealerSocket();
        socket.Options.Identity = System.Text.Encoding.UTF8.GetBytes(_workerId);
        socket.Connect("tcp://localhost:5555");

        // Tell broker I'm ready & Make sure broker has accepted our Ready before doing anything
        EnsureRegistered(socket);

        // Start background heartbeat task
        Task.Run(() => HeartbeatLoop(socket, _cts.Token, _workerId));

        while (true)
        {
            var msg = socket.ReceiveMultipartMessage();
            var wire = WireMessage.FromNetMQMessage(msg);

            if (wire.Command == ControlCommand.JobDispatch)
            {
                Console.WriteLine($"Worker {_workerId} got job {wire.JobId}");
                DoHeavyWork(wire.Payload);

                // Send progress
                socket.SendMultipartMessage(WireMessage.CreateWithJson(ControlCommand.ProgressUpdate, wire.JobId, _workerId, new { progress = 50 }).ToNetMQMessage());

                // Send payload to databridge via existing broker connection
                var wdt = new WorkerDataTransfer();
                wdt.TransferData(socket).GetAwaiter().GetResult();
            }

            //Tell broker I'm ready
            socket.SendMultipartMessage(new WireMessage(ControlCommand.JobDone, wire.JobId, _workerId).ToNetMQMessage());
            EnsureRegistered(socket);
        }
    }

    private void EnsureRegistered(DealerSocket socket)
    {
        var readyMsg = new WireMessage(ControlCommand.Ready, Guid.Empty, _workerId);

        while (true)
        {
            socket.SendMultipartMessage(readyMsg.ToNetMQMessage());
            Console.WriteLine($"Worker {_workerId} sent READY, awaiting ACK...");

            NetMQMessage? msg = null;
            if (socket.TryReceiveMultipartMessage(TimeSpan.FromSeconds(3), ref msg))
            {
                var wire = WireMessage.FromNetMQMessage(msg);
                if (wire.Command == ControlCommand.ReadyAck)
                {
                    Console.WriteLine($"Worker {_workerId} registered with broker.");
                    return;
                }
                else
                {
                    Console.WriteLine($"Worker {_workerId} got unexpected {wire.Command}, retrying READY...");
                }
            }
            else
            {
                Console.WriteLine($"Worker {_workerId} no ACK, retrying READY...");
            }
        }
    }


    private void DoHeavyWork(byte[] payload)
    {
        var payloadString = System.Text.Encoding.UTF8.GetString(payload);
        Console.WriteLine($"Starting heavy work on payload: {payloadString}");
        // Example: 5 steps, 2s each
        for (int i = 0; i < 5; i++)
        {
            Console.WriteLine($"Working... step {i + 1}/5");

            Thread.Sleep(2000);
        }
        Console.WriteLine($"Finished heavy work for payload: {payloadString}");
    }

    private static void HeartbeatLoop(DealerSocket socket, CancellationToken token, string workerId)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var hb = new WireMessage(ControlCommand.Ready, Guid.Empty, WorkerId: workerId);
                socket.SendMultipartMessage(hb.ToNetMQMessage());
                Console.WriteLine($"Worker {workerId} sent heartbeat.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Worker {workerId} heartbeat error: {ex.Message}");
            }
            var delay = TimeSpan.FromMinutes(1);
            Task.Delay(delay, token).Wait(token);
        }
    }
}
