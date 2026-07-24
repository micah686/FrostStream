using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Renditions;

public sealed class RenditionQueueConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<RenditionQueueConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<RenditionQueueListRequest>(
            messageBus,
            RenditionQueueSubjects.List,
            HandleListAsync,
            queueGroup: RenditionQueueSubjects.QueueGroup,
            cancellationToken: stoppingToken);
    }

    private async Task HandleListAsync(IMessageContext<RenditionQueueListRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRenditionQueueRepository>();
            var page = await repo.QueryAsync(context.Message);

            await context.RespondAsync(new RenditionQueueListResponse
            {
                Success = true,
                Items = page.Items,
                NextCursor = page.NextCursor,
                TotalCount = page.TotalCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling rendition queue list query.");
            await context.RespondAsync(new RenditionQueueListResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal rendition queue service error."
            });
        }
    }
}
