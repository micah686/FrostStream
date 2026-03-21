using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace Worker.Services;

public class StartupService(ILogger<StartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Setting up worker...");
        await SetupWorkerAsync();
        logger.LogInformation("Worker setup complete.");
    }

    private async Task SetupWorkerAsync()
    {
        try
        {
            Directory.CreateDirectory("tools");
            var ytDl = new YoutubeDL();
            logger.LogDebug("Downloading binaries...");
            await Utils.DownloadBinaries(true, "tools", true);
            logger.LogDebug("Binaries downloaded.");
            return;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            throw;
        }
    }
}