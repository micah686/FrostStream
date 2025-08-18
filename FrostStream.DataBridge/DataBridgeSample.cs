using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.DataBridge;

public class DataBridgeSample
{
    public void Run()
    {
        using var socket = new DealerSocket(">tcp://localhost:5557");
        while (true)
        {
            var msg = socket.ReceiveMultipartMessage();
            var wire = WireMessage.FromNetMQMessage(msg);

            if (wire.Command == ControlCommand.PayloadToDataBridge)
            {
                Console.WriteLine("Databridge got payload: " + System.Text.Encoding.UTF8.GetString(wire.Payload));
                // Send ACK
                var ack = new WireMessage(ControlCommand.PayloadAck, wire.JobId, wire.WorkerId);
                socket.SendMultipartMessage(ack.ToNetMQMessage());
            }
        }
    }
}