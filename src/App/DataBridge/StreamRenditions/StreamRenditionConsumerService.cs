using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.StreamRenditions;

public sealed class StreamRenditionConsumerService(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    IServiceScopeFactory scopeFactory,
    ILogger<StreamRenditionConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<StreamRenditionResolveRequest>(
            messageBus,
            StreamRenditionSubjects.Resolve,
            HandleResolveAsync,
            queueGroup: StreamRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<StreamRenditionClaimRequest>(
            messageBus,
            StreamRenditionSubjects.Claim,
            HandleClaimAsync,
            queueGroup: StreamRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<StreamRenditionCompleteRequest>(
            messageBus,
            StreamRenditionSubjects.Complete,
            HandleCompleteAsync,
            queueGroup: StreamRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<StreamRenditionFailRequest>(
            messageBus,
            StreamRenditionSubjects.Fail,
            HandleFailAsync,
            queueGroup: StreamRenditionSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);
    }

    private async Task HandleResolveAsync(IMessageContext<StreamRenditionResolveRequest> context)
    {
        try
        {
            var request = context.Message;
            StreamRenditionDto? item;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IStreamRenditionRepository>();
                item = request.CreateIfMissing
                    ? await repo.CreateIfMissingAsync(request.MediaGuid, request.StorageKey, request.SourceVersion)
                    : await repo.ResolveAsync(request.MediaGuid, request.StorageKey, request.SourceVersion);
            }

            if (item is null)
            {
                await context.RespondAsync(new StreamRenditionResolveResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Stream rendition source media '{request.MediaGuid}' was not found."
                });
                return;
            }

            if (request.CreateIfMissing && item.Status is StreamRenditionStatus.Pending or StreamRenditionStatus.Failed)
            {
                await publisher.PublishAsync(
                    BackgroundJobSubjects.StreamRenditionEncodeRequest,
                    new StreamRenditionEncodeRequested
                    {
                        RenditionId = item.RenditionId,
                        MediaGuid = item.MediaGuid,
                        SourceVersion = item.SourceVersion
                    },
                    messageId: item.RenditionId.ToString("N"));
            }

            await context.RespondAsync(new StreamRenditionResolveResponse { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving stream rendition for {MediaGuid}.", context.Message.MediaGuid);
            await context.RespondAsync(new StreamRenditionResolveResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal stream rendition service error."
            });
        }
    }

    private async Task HandleClaimAsync(IMessageContext<StreamRenditionClaimRequest> context)
    {
        try
        {
            StreamRenditionWorkItem? item;
            using (var scope = scopeFactory.CreateScope())
            {
                item = await scope.ServiceProvider
                    .GetRequiredService<IStreamRenditionRepository>()
                    .ClaimAsync(context.Message.RenditionId);
            }

            await context.RespondAsync(item is null
                ? new StreamRenditionClaimResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Stream rendition was not claimable." }
                : new StreamRenditionClaimResponse { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed claiming stream rendition {RenditionId}.", context.Message.RenditionId);
            await context.RespondAsync(new StreamRenditionClaimResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal stream rendition service error."
            });
        }
    }

    private async Task HandleCompleteAsync(IMessageContext<StreamRenditionCompleteRequest> context)
    {
        var request = context.Message;
        using var scope = scopeFactory.CreateScope();
        var success = await scope.ServiceProvider.GetRequiredService<IStreamRenditionRepository>()
            .CompleteAsync(request.RenditionId, request.StoragePath, request.SizeBytes, request.DurationSeconds);
        await context.RespondAsync(new StreamRenditionCompleteResponse
        {
            Success = success,
            ErrorCode = success ? null : "not_found",
            ErrorMessage = success ? null : "Stream rendition was not found."
        });
    }

    private async Task HandleFailAsync(IMessageContext<StreamRenditionFailRequest> context)
    {
        var request = context.Message;
        using var scope = scopeFactory.CreateScope();
        var success = await scope.ServiceProvider.GetRequiredService<IStreamRenditionRepository>()
            .FailAsync(request.RenditionId, request.ErrorMessage);
        await context.RespondAsync(new StreamRenditionFailResponse { Success = success });
    }
}
