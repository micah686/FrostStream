using System;
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

        public async Task ReceiveData()
        {
            server = new WatsonTcpServer("127.0.0.1", 9000);
            server.Events.ClientConnected += (s, e) =>
            {
                Console.WriteLine("Client connected: " + e.Client.ToString());
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

        static void MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (e.Metadata != null && e.Metadata.ContainsKey(TransferMessage.MetaData.ToString()))
            {
                // JSON transfer
                jsonStream.Write(e.Data, 0, e.Data.Length);
                Console.WriteLine($"Received JSON chunk ({e.Data.Length} bytes).");

                // Check for EOF
                if (e.Metadata.ContainsKey(TransferMessage.MetaData_EOF.ToString()))
                {
                    Console.WriteLine("JSON transfer complete. Deserializing...");

                    jsonStream.Position = 0;
                    using var reader = new StreamReader(jsonStream, Encoding.UTF8);
                    string jsonString = reader.ReadToEnd();

                    // Reset MemoryStream for future use
                    jsonStream.Dispose();
                    jsonStream = new MemoryStream();

                    // Deserialize into your object
                    FileTransferMetadata metaData = JsonConvert.DeserializeObject<FileTransferMetadata>(jsonString);
                    totalSize = (long)metaData.TotalSizeBytes;

                    Console.WriteLine($"Deserialized TransferMetaData: {JsonConvert.SerializeObject(metaData)}");
                }
            }
            else
            {
                // Video transfer
                if (videoStream == null)
                {
                    videoStream = new FileStream("received_bigvideo.mp4", FileMode.Create, FileAccess.Write);
                }

                videoStream.Write(e.Data, 0, e.Data.Length);
                receivedBytes += e.Data.Length;
                Console.WriteLine($"Received {receivedBytes}/{totalSize} bytes ({(receivedBytes * 100.0 / totalSize):F2}%)");
                if (e.Metadata != null && e.Metadata.ContainsKey(TransferMessage.File_EOF.ToString()))
                {
                    Console.WriteLine("Video transfer complete. Closing file.");
                    videoStream.Dispose();
                    videoStream = null;
                }
            }
        }

        
    }
}
