using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.Worker;

internal static class Heartbeat
{
    internal static void HeartbeatLoop(DealerSocket socket, CancellationToken token, string workerId)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var hb = new WireMessage(ControlCommand.Ready, Guid.Empty, WorkerId:workerId);
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
