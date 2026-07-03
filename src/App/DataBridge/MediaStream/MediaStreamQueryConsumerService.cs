using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class MediaStreamQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<MediaStreamQueryConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MediaStreamResolveRequestMessage>(
            messageBus,
            MediaStreamSubjects.Resolve,
            HandleResolveAsync,
            queueGroup: MediaStreamSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MediaThumbnailResolveRequestMessage>(
            messageBus,
            MediaStreamSubjects.ResolveThumbnail,
            HandleResolveThumbnailAsync,
            queueGroup: MediaStreamSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MediaCaptionResolveRequestMessage>(
            messageBus,
            MediaStreamSubjects.ResolveCaption,
            HandleResolveCaptionAsync,
            queueGroup: MediaStreamSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<AccountAssetResolveRequestMessage>(
            messageBus,
            MediaStreamSubjects.ResolveAccountAsset,
            HandleResolveAccountAssetAsync,
            queueGroup: MediaStreamSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);
    }

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

    private async Task HandleResolveCaptionAsync(IMessageContext<MediaCaptionResolveRequestMessage> context)
    {
        try
        {
            var request = context.Message;
            var item = await scopeFactory.WithScopedAsync<IMediaCaptionReadService, MediaCaptionLocationDto?>(
                service => service.ResolveAsync(
                    request.MediaGuid,
                    request.LanguageCode,
                    request.CaptionType));

            await context.RespondAsync(item is null
                ? new MediaCaptionResolveResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Caption '{request.LanguageCode}' for '{request.MediaGuid}' was not found."
                }
                : new MediaCaptionResolveResponseMessage
                {
                    Success = true,
                    Item = item
                });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed resolving caption for {MediaGuid}.",
                context.Message.MediaGuid);

            await context.RespondAsync(new MediaCaptionResolveResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal caption service error."
            });
        }
    }

    private async Task HandleResolveThumbnailAsync(IMessageContext<MediaThumbnailResolveRequestMessage> context)
    {
        try
        {
            var request = context.Message;
            var item = await scopeFactory.WithScopedAsync<IMediaThumbnailReadService, MediaThumbnailLocationDto?>(
                service => service.ResolveAsync(request.MediaGuid));

            await context.RespondAsync(item is null
                ? new MediaThumbnailResolveResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Thumbnail for '{request.MediaGuid}' was not found."
                }
                : new MediaThumbnailResolveResponseMessage
                {
                    Success = true,
                    Item = item
                });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed resolving thumbnail for {MediaGuid}.",
                context.Message.MediaGuid);

            await context.RespondAsync(new MediaThumbnailResolveResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal thumbnail service error."
            });
        }
    }

    private async Task HandleResolveAccountAssetAsync(IMessageContext<AccountAssetResolveRequestMessage> context)
    {
        try
        {
            var request = context.Message;
            var item = await scopeFactory.WithScopedAsync<IAccountAssetReadService, AccountAssetLocationDto?>(
                service => service.ResolveAsync(request.AccountId, request.AssetType));

            await context.RespondAsync(item is null
                ? new AccountAssetResolveResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"{request.AssetType} for account '{request.AccountId}' was not found."
                }
                : new AccountAssetResolveResponseMessage
                {
                    Success = true,
                    Item = item
                });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed resolving {AssetType} for account {AccountId}.",
                context.Message.AssetType,
                context.Message.AccountId);

            await context.RespondAsync(new AccountAssetResolveResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal account asset service error."
            });
        }
    }
}
