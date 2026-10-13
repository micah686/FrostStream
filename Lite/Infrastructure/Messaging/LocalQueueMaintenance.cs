using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace DataBridge.Messaging;

internal sealed class LocalQueueMaintenance(SqliteDurableTransport transport, ILogger<LocalQueueMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try { for (var page = 0; page < 8; page++) await transport.MaintainAsync(stoppingToken); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested) { logger.LogError(error, "Local queue retention failed"); }
    }
}
