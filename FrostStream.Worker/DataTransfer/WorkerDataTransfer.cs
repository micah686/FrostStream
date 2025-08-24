using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using WatsonTcp;

namespace FrostStream.Worker.DataTransfer;

internal class WorkerDataTransfer
{
    public async Task TransferData(DealerSocket brokerSocket, Guid jobId, string workerId, string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine("File not found: " + filePath);
            return;
        }

        var fi = new FileInfo(filePath);
        long totalSize = fi.Length;

        var (leaseId, port) = AcquireLease(brokerSocket, jobId, workerId, totalSize);

        using var client = new WatsonTcpClient("127.0.0.1", port);
        client.Settings.Guid = Globals.WorkerId;

        bool connected = false;
        foreach (var delay in new[] {1,5,10,15,20,25,30})
        {
            try { client.Connect(); connected = true; break; }
            catch { Thread.Sleep(TimeSpan.FromSeconds(delay)); }
        }
        if (!connected) return;

        var hash = await ComputeXxHash3Async(filePath);

        var meta = new FileTransferMetadata
        {
            JobId = jobId,
            WorkerId = workerId,
            LeaseId = leaseId,
            FileName = fi.Name,
            TotalSizeBytes = (ulong)totalSize,
            Hash = hash
        };

        await SendJson(client, meta);
        await SendFile(client, filePath);

        // Wait for ACK/NACK from DataBridge via broker
        while (true)
        {
            var msg = brokerSocket.ReceiveMultipartMessage();
            var wire = WireMessage.FromNetMQMessage(msg);
            if (wire.Command == ControlCommand.PayloadAck)
            {
                Console.WriteLine("Transfer acknowledged by DataBridge.");
                break;
            }
            if (wire.Command == ControlCommand.PayloadNack)
            {
                Console.WriteLine("Transfer failed: NACK received.");
                break;
            }
        }
    }

    private (Guid leaseId, int port) AcquireLease(DealerSocket socket, Guid jobId, string workerId, long size)
    {
        while (true)
        {
            var req = new TransferRequest(jobId, workerId, size);
            var wire = WireMessage.CreateWithJson(ControlCommand.TransferReserve, jobId, workerId, req);
            socket.SendMultipartMessage(wire.ToNetMQMessage());

            var msg = socket.ReceiveMultipartMessage();
            var resp = WireMessage.FromNetMQMessage(msg);
            if (resp.Command == ControlCommand.TransferGranted)
            {
                var granted = resp.GetJsonPayload<TransferGranted>();
                return (granted.LeaseId, granted.Port);
            }
            if (resp.Command == ControlCommand.TransferDenied)
            {
                var denied = resp.GetJsonPayload<TransferDenied>();
                Console.WriteLine($"Lease denied, retrying in {denied.RetryAfterSeconds}s");
                Thread.Sleep(TimeSpan.FromSeconds(denied.RetryAfterSeconds));
            }
        }
    }

    private async Task SendJson(WatsonTcpClient client, FileTransferMetadata meta)
    {
        var json = JsonConvert.SerializeObject(meta);
        var bytes = Encoding.UTF8.GetBytes(json);
        int chunkSize = 1024 * 1024;
        int offset = 0;
        while (offset < bytes.Length)
        {
            int size = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = new byte[size];
            Array.Copy(bytes, offset, chunk, 0, size);
            offset += size;
            var md = new Dictionary<string, object> { { TransferMessage.MetaData.ToString(), true } };
            if (offset >= bytes.Length)
                md.Add(TransferMessage.MetaData_EOF.ToString(), true);
            await client.SendAsync(chunk, md);
        }
    }

    private async Task SendFile(WatsonTcpClient client, string filePath)
    {
        const int chunkSize = 1024 * 1024;
        byte[] buffer = new byte[chunkSize];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var sw = Stopwatch.StartNew();
        long sent = 0;
        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = new byte[bytesRead];
            Array.Copy(buffer, chunk, bytesRead);
            if (fs.Position == fs.Length)
            {
                var meta = new Dictionary<string, object>{{TransferMessage.File_EOF.ToString(), true}};
                await client.SendAsync(chunk, meta);
            }
            else
            {
                await client.SendAsync(chunk);
            }
            sent += bytesRead;
            double speed = sent / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"Sent {sent}/{fs.Length} bytes ({sent * 100.0 / fs.Length:F2}%) at {speed:F2} MB/s");
        }
    }

    public static async Task<ulong> ComputeXxHash3Async(string filePath)
    {
        int bufferSize = 1024 * 1024;
        var xx = new XxHash3();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        var buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            xx.Append(buffer.AsSpan(0, bytesRead));
        }
        return xx.GetCurrentHashAsUInt64();
    }
}
