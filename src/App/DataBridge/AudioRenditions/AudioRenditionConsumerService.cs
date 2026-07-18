using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.AudioRenditions;

public sealed class AudioRenditionConsumerService(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    IServiceScopeFactory scopeFactory,
    ILogger<AudioRenditionConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<AudioRenditionResolveRequest>(
            messageBus,
            AudioRenditionSubjects.Resolve,
            HandleResolveAsync,
            queueGroup: AudioRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<ChannelAudioResolveRequest>(
            messageBus,
            AudioRenditionSubjects.ResolveChannel,
            HandleResolveChannelAsync,
            queueGroup: AudioRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<AudioRenditionClaimRequest>(
            messageBus,
            AudioRenditionSubjects.Claim,
            HandleClaimAsync,
            queueGroup: AudioRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<AudioRenditionCompleteRequest>(
            messageBus,
            AudioRenditionSubjects.Complete,
            HandleCompleteAsync,
            queueGroup: AudioRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<AudioRenditionFailRequest>(
            messageBus,
            AudioRenditionSubjects.Fail,
            HandleFailAsync,
            queueGroup: AudioRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);
    }

    private async Task HandleResolveChannelAsync(IMessageContext<ChannelAudioResolveRequest> context)
    {
        try
        {
            ChannelAudioResolveResult? result;
            using (var scope = scopeFactory.CreateScope())
            {
                result = await scope.ServiceProvider.GetRequiredService<IAudioRenditionRepository>()
                    .ResolveChannelAsync(
                        context.Message.AccountId,
                        context.Message.CreateIfMissing,
                        context.Message.RetryFailedAndPending);
            }

            if (result is null)
            {
                await context.RespondAsync(new ChannelAudioResolveResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Channel '{context.Message.AccountId}' was not found."
                });
                return;
            }

            foreach (var rendition in result.RenditionsToQueue)
            {
                await publisher.PublishAsync(
                    BackgroundJobSubjects.AudioRenditionEncodeRequest,
                    new AudioRenditionEncodeRequested
                    {
                        RenditionId = rendition.RenditionId,
                        MediaGuid = rendition.MediaGuid,
                        SourceVersion = rendition.SourceVersion
                    },
                    messageId: rendition.RenditionId.ToString("N"));
            }

            await context.RespondAsync(new ChannelAudioResolveResponse { Success = true, Item = result.Channel });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving channel audio for account {AccountId}.", context.Message.AccountId);
            await context.RespondAsync(new ChannelAudioResolveResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal channel audio service error."
            });
        }
    }

    private async Task HandleResolveAsync(IMessageContext<AudioRenditionResolveRequest> context)
    {
        try
        {
            var request = context.Message;
            AudioRenditionDto? item;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IAudioRenditionRepository>();
                item = request.CreateIfMissing
                    ? await repo.CreateIfMissingAsync(request.MediaGuid, request.StorageKey, request.SourceVersion)
                    : await repo.ResolveAsync(request.MediaGuid, request.StorageKey, request.SourceVersion);
            }

            if (item is null)
            {
                await context.RespondAsync(new AudioRenditionResolveResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Audio rendition source media '{request.MediaGuid}' was not found."
                });
                return;
            }

            if (request.CreateIfMissing && item.Status is AudioRenditionStatus.Pending or AudioRenditionStatus.Failed)
            {
                await publisher.PublishAsync(
                    BackgroundJobSubjects.AudioRenditionEncodeRequest,
                    new AudioRenditionEncodeRequested
                    {
                        RenditionId = item.RenditionId,
                        MediaGuid = item.MediaGuid,
                        SourceVersion = item.SourceVersion
                    },
                    messageId: item.RenditionId.ToString("N"));
            }

            await context.RespondAsync(new AudioRenditionResolveResponse { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving audio rendition for {MediaGuid}.", context.Message.MediaGuid);
            await context.RespondAsync(new AudioRenditionResolveResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal audio rendition service error."
            });
        }
    }

    private async Task HandleClaimAsync(IMessageContext<AudioRenditionClaimRequest> context)
    {
        try
        {
            AudioRenditionWorkItem? item;
            using (var scope = scopeFactory.CreateScope())
            {
                item = await scope.ServiceProvider
                    .GetRequiredService<IAudioRenditionRepository>()
                    .ClaimAsync(context.Message.RenditionId);
            }

            await context.RespondAsync(item is null
                ? new AudioRenditionClaimResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Audio rendition was not claimable." }
                : new AudioRenditionClaimResponse { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed claiming audio rendition {RenditionId}.", context.Message.RenditionId);
            await context.RespondAsync(new AudioRenditionClaimResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal audio rendition service error."
            });
        }
    }

    private async Task HandleCompleteAsync(IMessageContext<AudioRenditionCompleteRequest> context)
    {
        var request = context.Message;
        using var scope = scopeFactory.CreateScope();
        var success = await scope.ServiceProvider.GetRequiredService<IAudioRenditionRepository>()
            .CompleteAsync(request.RenditionId, request.StoragePath, request.ContentHashXxh128, request.SizeBytes, request.DurationSeconds);
        await context.RespondAsync(new AudioRenditionCompleteResponse
        {
            Success = success,
            ErrorCode = success ? null : "not_found",
            ErrorMessage = success ? null : "Audio rendition was not found."
        });
    }

    private async Task HandleFailAsync(IMessageContext<AudioRenditionFailRequest> context)
    {
        var request = context.Message;
        using var scope = scopeFactory.CreateScope();
        var success = await scope.ServiceProvider.GetRequiredService<IAudioRenditionRepository>()
            .FailAsync(request.RenditionId, request.ErrorMessage);
        await context.RespondAsync(new AudioRenditionFailResponse { Success = success });
    }
}
