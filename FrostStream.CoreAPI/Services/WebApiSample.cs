using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.CoreAPI.Services;

public class WebApiSample
{
    public void SendJob(Guid jobId, object payload)
    {
        using var socket = new DealerSocket(">tcp://localhost:5556");
        var msg = WireMessage.CreateWithJson(ControlCommand.JobDispatch, jobId, null, payload);
        socket.SendMultipartMessage(msg.ToNetMQMessage());
    }

    public void Listen()
    {
        using var socket = new DealerSocket(">tcp://localhost:5556");
        while (true)
        {
            var msg = socket.ReceiveMultipartMessage();
            var wire = WireMessage.FromNetMQMessage(msg);
            Console.WriteLine($"WebAPI got {wire.Command} from worker {wire.WorkerId}");
        }
    }
}