using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.MediaContent;

public sealed class MediaContentQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<MediaContentQueryConsumerService> logger) : SubscriptionBackgroundService
{
    protected override Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
        => SubscribeAsync<MediaContentResolveRequestMessage>(
            messageBus,
            MediaContentSubjects.Resolve,
            HandleResolveAsync,
            queueGroup: MediaContentSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

    private async Task HandleResolveAsync(IMessageContext<MediaContentResolveRequestMessage> context)
    {
        try
        {
            var request = context.Message;
            var item = await scopeFactory.WithScopedAsync<IMediaContentReadService, MediaContentLocationDto?>(
                service => service.ResolveAsync(
                    request.MediaGuid,
                    request.StorageKey,
                    request.Version));

            await context.RespondAsync(item is null
                ? new MediaContentResolveResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Media content for '{request.MediaGuid}' was not found."
                }
                : new MediaContentResolveResponseMessage
                {
                    Success = true,
                    Item = item
                });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed resolving media content for {MediaGuid}.",
                context.Message.MediaGuid);

            await context.RespondAsync(new MediaContentResolveResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal media content service error."
            });
        }
    }
}
