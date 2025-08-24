using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FrostStream.Shared;
using Newtonsoft.Json;
using WatsonTcp;

namespace FrostStream.DataBridge
{
    internal class DataReciever
    {
        static string outputFile;
        static long totalSize;
        static long receivedBytes = 0;
        static WatsonTcpServer server;
        static MemoryStream jsonStream = new MemoryStream();
        static FileStream videoStream;
        ConcurrentDictionary<Guid, TransferState> _transfers = new();

        public async Task ReceiveData()
        {
            server = new WatsonTcpServer("127.0.0.1", 9000);
            server.Events.ClientConnected += (s, e) =>
            {
                Console.WriteLine("Client connected: " + e.Client.ToString());
                _transfers.TryAdd(e.Client.Guid, new TransferState());
            };

            server.Events.ClientDisconnected += (s, e) =>
            {
                Console.WriteLine("Client disconnected");
                videoStream?.Close();
            };
            server.Events.MessageReceived += MessageReceived;
            

            server.Start();
            Console.WriteLine("Receiver started. Press ENTER to quit.");
            Console.ReadLine();
        }

        void MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (e.Metadata != null && e.Metadata.ContainsKey(TransferMessage.MetaData.ToString()))
            {
                if(_transfers[e.Client.Guid].JsonMetaDataStream == null)
                    _transfers[e.Client.Guid].JsonMetaDataStream = new MemoryStream();
                // JSON transfer
                _transfers[e.Client.Guid].JsonMetaDataStream.Write(e.Data, 0, e.Data.Length);
                Console.WriteLine($"Received JSON chunk ({e.Data.Length} bytes).");

                // Check for EOF
                if (e.Metadata.ContainsKey(TransferMessage.MetaData_EOF.ToString()))
                {
                    Console.WriteLine("JSON transfer complete. Deserializing...");

                    _transfers[e.Client.Guid].JsonMetaDataStream.Position = 0;

                    using var reader2 = new StreamReader(_transfers[e.Client.Guid].JsonMetaDataStream, Encoding.UTF8);
                    string jsonString2 = reader2.ReadToEnd();

                    // Reset MemoryStream for future use
                    _transfers[e.Client.Guid].JsonMetaDataStream.Dispose();

                    // Deserialize into your object
                    FileTransferMetadata metaData2 = JsonConvert.DeserializeObject<FileTransferMetadata>(jsonString2);
                    totalSize = (long)metaData2.TotalSizeBytes;

                    Console.WriteLine($"Deserialized TransferMetaData: {JsonConvert.SerializeObject(metaData2)}");
                }
            }
            else
            {

                // Video transfer
                if (_transfers[e.Client.Guid].MediaStream == null)
                {
                    _transfers[e.Client.Guid].MediaStream = new FileStream("received_bigvideo.mp4", FileMode.Create, FileAccess.Write);
                }

                _transfers[e.Client.Guid].MediaStream.Write(e.Data, 0, e.Data.Length);
                receivedBytes += e.Data.Length;
                Console.WriteLine($"Received {receivedBytes}/{totalSize} bytes ({(receivedBytes * 100.0 / totalSize):F2}%)");
                if (e.Metadata != null && e.Metadata.ContainsKey(TransferMessage.File_EOF.ToString()))
                {
                    Console.WriteLine("Video transfer complete. Closing file.");
                    _transfers[e.Client.Guid].MediaStream.Dispose();
                    _transfers[e.Client.Guid].MediaStream = null;
                }
            }
        }
        
    }

    internal class TransferState
    {
        public MemoryStream JsonMetaDataStream { get; set; }
        public FileStream MediaStream { get; set; }
    }
}
