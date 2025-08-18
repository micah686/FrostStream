using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.CoreAPI.Services;

public class JobQueueService : IDisposable
{
    private readonly DealerSocket _socket;
    private readonly CancellationTokenSource _cts = new();

    public JobQueueService()
    {
        _socket = new DealerSocket();
        _socket.Connect("tcp://localhost:5556"); // Broker endpoint for WebAPI
        Console.WriteLine("JobQueueService connected to broker.");

        // Optional: background task to listen for broker responses
        Task.Run(() => ListenLoop(_cts.Token), _cts.Token);
    }

    public Guid EnqueueJob(string payload)
    {
        var jobId = Guid.NewGuid();
        var wire = WireMessage.CreateWithJson(
            ControlCommand.JobDispatch,
            jobId,
            null, // WebAPI isn’t a worker
            new { payload }
        );

        _socket.SendMultipartMessage(wire.ToNetMQMessage());
        Console.WriteLine($"Queued job {jobId} with payload: {payload}");
        return jobId;
    }

    private void ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                NetMQMessage? msg = null;
                if (_socket.TryReceiveMultipartMessage(TimeSpan.FromMilliseconds(500), ref msg))
                {
                    var wire = WireMessage.FromNetMQMessage(msg);
                    Console.WriteLine($"[WebAPI] Received {wire.Command} from worker {wire.WorkerId}");
                    // Here you could persist progress or job done events
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebAPI] ListenLoop error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Dispose();
    }
}