using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.AudioRenditions;

public sealed class MediaEncodingStatusConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<MediaEncodingStatusConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<SetMediaEncodedStatusRequest>(
            messageBus,
            AudioEncodingStatusSubjects.Set,
            HandleSetAsync,
            queueGroup: AudioEncodingStatusSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<SetMediaEncodedStatusByMediaGuidRequest>(
            messageBus,
            AudioEncodingStatusSubjects.SetByMediaGuid,
            HandleSetByMediaGuidAsync,
            queueGroup: AudioEncodingStatusSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<ListChannelEncodingStatusRequest>(
            messageBus,
            AudioEncodingStatusSubjects.ListChannel,
            HandleListChannelAsync,
            queueGroup: AudioEncodingStatusSubjects.QueueGroup,
            cancellationToken: stoppingToken);
    }

    private async Task HandleSetAsync(IMessageContext<SetMediaEncodedStatusRequest> context)
    {
        try
        {
            var request = context.Message;
            using var scope = scopeFactory.CreateScope();
            var item = await scope.ServiceProvider.GetRequiredService<IMediaEncodingStatusRepository>()
                .SetAsync(request.AccountId, request.MediaGuid, request.IsEncoded, request.StorageKey, request.StoragePath);

            if (item is null)
            {
                await context.RespondAsync(new SetMediaEncodedStatusResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Media '{request.MediaGuid}' was not found in channel '{request.AccountId}'."
                });
                return;
            }

            await context.RespondAsync(new SetMediaEncodedStatusResponse { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed setting audio encoding status for media {MediaGuid}.", context.Message.MediaGuid);
            await context.RespondAsync(new SetMediaEncodedStatusResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal audio encoding status service error."
            });
        }
    }

    private async Task HandleSetByMediaGuidAsync(IMessageContext<SetMediaEncodedStatusByMediaGuidRequest> context)
    {
        try
        {
            var request = context.Message;
            using var scope = scopeFactory.CreateScope();
            var item = await scope.ServiceProvider.GetRequiredService<IMediaEncodingStatusRepository>()
                .SetByMediaGuidAsync(request.MediaGuid, request.IsEncoded, request.StorageKey, request.StoragePath);

            if (item is null)
            {
                await context.RespondAsync(new SetMediaEncodedStatusResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Media '{request.MediaGuid}' was not found."
                });
                return;
            }

            await context.RespondAsync(new SetMediaEncodedStatusResponse { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed setting audio encoding status for media {MediaGuid}.", context.Message.MediaGuid);
            await context.RespondAsync(new SetMediaEncodedStatusResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal audio encoding status service error."
            });
        }
    }

    private async Task HandleListChannelAsync(IMessageContext<ListChannelEncodingStatusRequest> context)
    {
        try
        {
            var request = context.Message;
            using var scope = scopeFactory.CreateScope();
            var page = await scope.ServiceProvider.GetRequiredService<IMediaEncodingStatusRepository>()
                .ListChannelAsync(request.AccountId, request.IsEncoded, request.StorageKey, request.Limit, request.Cursor);

            await context.RespondAsync(new ListChannelEncodingStatusResponse
            {
                Success = true,
                Items = page.Items,
                NextCursor = page.NextCursor,
                TotalCount = page.TotalCount,
                EncodedCount = page.EncodedCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing audio encoding status for account {AccountId}.", context.Message.AccountId);
            await context.RespondAsync(new ListChannelEncodingStatusResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal audio encoding status service error."
            });
        }
    }
}
