using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Threading.Tasks;
using FrostStream.Shared;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using WatsonTcp;

namespace FrostStream.Worker.DataTransfer
{
    internal class WorkerDataTransfer
    {
        public async Task TransferData(DealerSocket brokerSocket)
        {
            // Acquire lease from DataBridge via the existing broker connection
            bool leaseGranted = false;
            while (!leaseGranted)
            {
                var req = new WireMessage(ControlCommand.TransferLeaseRequest, Guid.Empty, Globals.WorkerId.ToString());
                brokerSocket.SendMultipartMessage(req.ToNetMQMessage());
                NetMQMessage? msg = null;
                if (brokerSocket.TryReceiveMultipartMessage(TimeSpan.FromSeconds(5), ref msg))
                {
                    var wire = WireMessage.FromNetMQMessage(msg);
                    if (wire.Command == ControlCommand.TransferGranted)
                    {
                        leaseGranted = true;
                        break;
                    }
                    else if (wire.Command == ControlCommand.TransferDenied)
                    {
                        var delay = Random.Shared.Next(10, 31);
                        Console.WriteLine($"Lease denied, retrying in {delay}s");
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                    }
                }
            }

            string filePath = "sample.mp4";
            if (!File.Exists(filePath))
            {
                Console.WriteLine("File not found: " + filePath);
                return;
            }

            FileInfo fi = new FileInfo(filePath);
            long totalSize = fi.Length;
            int chunkSize = 1024 * 1024; // 1 MB
            long sentBytes = 0;

            var client = new WatsonTcpClient("127.0.0.1", 9000);
            client.Settings.Guid = Globals.WorkerId;
            client.Events.MessageReceived += (s, e) =>
            {
                Console.WriteLine("Message from server: " + Encoding.UTF8.GetString(e.Data));
            };
            client.Events.ServerConnected += (s, e) =>
            {
                Console.WriteLine("Connected to server.");
            };
            bool connected = false;

            // Retry connection logic
            int[] retryIntervals = { 1000, 5000, 10000, 15000, 20000, 25000, 30000 }; //1 sec to 30 sec, 5 second increments
            foreach (int interval in retryIntervals)
            {
                try
                {
                    client.Connect();
                    connected = true;
                    break;
                }
                catch
                {
                    Thread.Sleep(interval);
                }
            }
            if (!connected) return;

            var xxHash = await ComputeXxHash3Async(filePath, new Progress<double>(p =>
            {
                Console.WriteLine($"Hashing progress: {p:F2}%");
            }));

            var ftu = new Shared.FileTransferMetadata
            {
                FileName = fi.Name,
                TotalSizeBytes = (ulong)totalSize,
                Hash = xxHash // Placeholder for hash value
            };
            await SendJson(client, ftu);

            await SendFile(client, filePath);
        }

        private async Task SendJson(WatsonTcpClient client, FileTransferMetadata transferMetadata)
        {
            var json = JsonConvert.SerializeObject(transferMetadata);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            int chunkSize = 1024 * 1024; // 1MB
            int offset = 0;

            while (offset < jsonBytes.Length)
            {
                int remaining = jsonBytes.Length - offset;
                int size = Math.Min(chunkSize, remaining);
                byte[] chunk = new byte[size];
                Array.Copy(jsonBytes, offset, chunk, 0, size);
                offset += size;

                var meta = new Dictionary<string, object>
                {
                    { TransferMessage.MetaData.ToString(), true }
                };

                // Mark last chunk
                if (offset >= jsonBytes.Length)
                {
                    meta.Add(TransferMessage.MetaData_EOF.ToString(), true);
                }

                await client.SendAsync(chunk, meta);
            }

            Console.WriteLine("JSON metadata sent from memory.");
        }

        private async Task SendFile(WatsonTcpClient client, string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine("File not found: " + filePath);
                return;
            }
            FileInfo fi = new FileInfo(filePath);
            long totalSize = fi.Length;
            int chunkSize = 1024 * 1024; // 1 MB
            long sentBytes = 0;

            // Start sending file chunks
            byte[] buffer = new byte[chunkSize];
            var sw = Stopwatch.StartNew();
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    byte[] chunk = new byte[bytesRead];
                    Array.Copy(buffer, chunk, bytesRead);
                    if (fs.Position == fs.Length)
                    {
                        var meta = new Dictionary<string, object>();
                        meta.Add(TransferMessage.File_EOF.ToString(), true);
                        await client.SendAsync(chunk, meta);
                    }
                    else
                    {
                        await client.SendAsync(chunk);
                    }

                    sentBytes += bytesRead;
                    double seconds = sw.Elapsed.TotalSeconds;
                    double speed = sentBytes / (1024.0 * 1024.0) / seconds;
                    Console.WriteLine($"Sent {sentBytes}/{totalSize} bytes ({(sentBytes * 100.0 / totalSize):F2}%) at {speed:F2} MB/s");
                }
            }
            Console.WriteLine("File transfer completed.");
        }

        public static async Task<ulong> ComputeXxHash3Async(string filePath, IProgress<double>? progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            int bufferSize = 1024 * 1024; // 1MB chunks
            var xxHash = new XxHash3();
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);

            var buffer = new byte[bufferSize];
            long totalBytesRead = 0;
            long fileSize = fileStream.Length;

            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                xxHash.Append(buffer.AsSpan(0, bytesRead));

                totalBytesRead += bytesRead;

                progress?.Report((double)totalBytesRead / fileSize * 100);
            }

            return xxHash.GetCurrentHashAsUInt64();
        }
    }
}
