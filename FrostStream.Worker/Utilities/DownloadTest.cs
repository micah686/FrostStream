using Newtonsoft.Json;
using YoutubeDLSharp.Metadata;

namespace FrostStream.Worker.Utilities
{
    internal class DownloadTest
    {
        internal static readonly string DATA_PATH = Path.Combine(Directory.GetCurrentDirectory(), "data");
        internal static readonly string TOOLS_PATH = Path.Combine(DATA_PATH, "tools");
        internal static readonly string DOWNLOAD_PATH = Path.Combine(DATA_PATH, "downloads");

        private static async Task Test()
        {
            var dlService = new DownloadService();
            await YoutubeDLSharp.Utils.DownloadBinaries(true, TOOLS_PATH);

            string jobId = "1";
            string url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
            try
            {
                await dlService.DownloadVideo(url, null, null);
                var downloadDir = Path.Combine(Program.DOWNLOAD_PATH, jobId, "youtube");
                var infoJsonFile = Directory.GetFiles(downloadDir, "*.info.json").FirstOrDefault();
                if (infoJsonFile == null)
                {
                    Console.WriteLine("Info JSON file not found.");
                    return;
                }
                var json = File.ReadAllText(infoJsonFile);
                var videoData = JsonConvert.DeserializeObject<VideoData>(json);
                if (videoData != null)
                {
                    Console.WriteLine($"Title: {videoData.Title}");
                    Console.WriteLine($"Uploader: {videoData.Uploader}");
                    Console.WriteLine($"Duration: {videoData.Duration} seconds");
                }
                else
                {
                    Console.WriteLine("Failed to deserialize video metadata.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }
    }
}
