using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class MediaStreamQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<MediaStreamQueryConsumerService> logger) : SubscriptionBackgroundService
{
    protected override Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
        => SubscribeAsync<MediaStreamResolveRequestMessage>(
            messageBus,
            MediaStreamSubjects.Resolve,
            HandleResolveAsync,
            queueGroup: MediaStreamSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

    private async Task HandleResolveAsync(IMessageContext<MediaStreamResolveRequestMessage> context)
    {
        try
        {
            var request = context.Message;
            var item = await scopeFactory.WithScopedAsync<IMediaStreamReadService, MediaStreamLocationDto?>(
                service => service.ResolveAsync(
                    request.MediaGuid,
                    request.StorageKey,
                    request.Version));

            await context.RespondAsync(item is null
                ? new MediaStreamResolveResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Media stream for '{request.MediaGuid}' was not found."
                }
                : new MediaStreamResolveResponseMessage
                {
                    Success = true,
                    Item = item
                });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed resolving media stream for {MediaGuid}.",
                context.Message.MediaGuid);

            await context.RespondAsync(new MediaStreamResolveResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal media stream service error."
            });
        }
    }
}
