using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Shared.Messaging;

namespace Worker.Services;

public sealed class MediaToolsUpdateConsumerService(
    IMessageBus bus,
    MediaToolsUpdateManager manager,
    ILogger<MediaToolsUpdateConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
        => await SubscribeAsync<MediaToolsUpdateRequest>(bus, MediaToolsSubjects.Update, HandleAsync,
            "worker-media-tools-update", stoppingToken);

    private async Task HandleAsync(IMessageContext<MediaToolsUpdateRequest> context)
    {
        try
        {
            await manager.UpdateNowAsync(CancellationToken.None);
            await context.RespondAsync(new MediaToolsUpdateResponse(true, "Media tools updated."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual media tool update failed.");
            await context.RespondAsync(new MediaToolsUpdateResponse(false, "Media tool update failed: " + ex.Message));
        }
    }
}
