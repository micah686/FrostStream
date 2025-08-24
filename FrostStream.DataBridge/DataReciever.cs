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

            //server.Events.MessageReceived += (s, e) =>
            //{

            //    // Metadata available in e.Metadata
            //    if (e.Metadata != null && e.Metadata.ContainsKey("filename"))
            //    {
            //        e.Metadata.TryGetValue("filename", out var fname);
            //        var je = fname as JsonElement?;
            //        var data = je?.ValueKind;

            //        outputFile = TryGetString(e.Metadata, "filename");
            //        totalSize = TryGetInt64(e.Metadata, "filesize");


            //        //outputFile = e.Metadata["filename"].ToString();                
            //        //totalSize = Convert.ToInt64(e.Metadata["filesize"]);

            //        Console.WriteLine($"Receiving file: {outputFile} ({totalSize} bytes)");
            //        fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
            //    }

            //    if( e.Metadata != null && e.Metadata.ContainsKey("metadata") && e.Data?.Length >0)
            //    {
            //        e.Metadata.TryGetValue("metadata", out var meta);
            //        var je = meta as JsonElement?;
                   
            //    }

            //    if (fs != null && e.Data?.Length > 0)
            //    {
            //        fs.Write(e.Data, 0, e.Data.Length);
            //        receivedBytes += e.Data.Length;
            //        Console.WriteLine($"Received {receivedBytes}/{totalSize} bytes ({(receivedBytes * 100.0 / totalSize):F2}%)");
            //    }
            //};

            server.Start();
            Console.WriteLine("Receiver started. Press ENTER to quit.");
            Console.ReadLine();
        }

        static void MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (e.Metadata != null && e.Metadata.ContainsKey("metadata"))
            {
                // JSON transfer
                jsonStream.Write(e.Data, 0, e.Data.Length);
                Console.WriteLine($"Received JSON chunk ({e.Data.Length} bytes).");

                // Check for EOF
                if (e.Metadata.ContainsKey("eof"))
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
            }
        }

        static string? TryGetString(Dictionary<string, object> md, string key)
        {
            if (!md.TryGetValue(key, out var val) || val is null) return null;

            if (val is string s) return s;

            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String) return je.GetString();
                // Fallback: number -> string
                if (je.ValueKind == JsonValueKind.Number) return je.GetRawText();
            }

            // Last resort: ToString()
            return val.ToString();
        }

        static long TryGetInt64(Dictionary<string, object> md, string key)
        {
            if (!md.TryGetValue(key, out var val) || val is null) return -1;

            switch (val)
            {
                case long l: return l;
                case int i: return i;
                case string s when long.TryParse(s, out var parsed): return parsed;
                case JsonElement je:
                    if (je.ValueKind == JsonValueKind.Number)
                    {
                        // If it's too large for Int64 this will throw – which is fine here.
                        if (je.TryGetInt64(out var n)) return n;
                        // If the number is encoded as a raw json number but TryGetInt64 failed
                        // (e.g., floating), try string path:
                        if (long.TryParse(je.GetRawText(), out var viaRaw)) return viaRaw;
                    }
                    if (je.ValueKind == JsonValueKind.String && long.TryParse(je.GetString(), out var viaStr))
                        return viaStr;
                    break;
            }

            // Fallback, best-effort parse
            if (long.TryParse(val.ToString(), out var last)) return last;

            return -1;
        }
    }
}
