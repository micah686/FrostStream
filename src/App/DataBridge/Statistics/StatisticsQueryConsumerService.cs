using DataBridge;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Statistics;

public sealed class StatisticsQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<StatisticsQueryConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<StatisticsOverviewRequestMessage>(
            messageBus,
            StatisticsSubjects.Overview,
            HandleOverviewAsync,
            queueGroup: StatisticsSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<StatisticsChannelsListRequestMessage>(
            messageBus,
            StatisticsSubjects.ChannelsList,
            HandleChannelsListAsync,
            queueGroup: StatisticsSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<StatisticsChannelGetRequestMessage>(
            messageBus,
            StatisticsSubjects.ChannelGet,
            HandleChannelGetAsync,
            queueGroup: StatisticsSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<StatisticsDownloadHistoryRequestMessage>(
            messageBus,
            StatisticsSubjects.DownloadHistory,
            HandleDownloadHistoryAsync,
            queueGroup: StatisticsSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to statistics query subjects.");
    }

    private async Task HandleOverviewAsync(IMessageContext<StatisticsOverviewRequestMessage> context)
    {
        try
        {
            var overview = await WithQuery(query => query.GetOverviewAsync(context.Message.OwnerSubject));
            await context.RespondAsync(new StatisticsOverviewResponseMessage
            {
                Success = true,
                Overview = overview
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling statistics overview query.");
            await context.RespondAsync(Failure<StatisticsOverviewResponseMessage>("internal_error", "Internal statistics service error."));
        }
    }

    private async Task HandleChannelsListAsync(IMessageContext<StatisticsChannelsListRequestMessage> context)
    {
        try
        {
            var result = await WithQuery(query => query.ListChannelsAsync(
                context.Message.PageSize,
                context.Message.Page,
                context.Message.SortBy,
                context.Message.SortOrder));

            await context.RespondAsync(new StatisticsChannelsListResponseMessage
            {
                Success = true,
                Items = result.Items,
                Page = result.Page,
                TotalCount = result.TotalCount,
                HasMore = result.HasMore
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling channel statistics list query.");
            await context.RespondAsync(Failure<StatisticsChannelsListResponseMessage>("internal_error", "Internal statistics service error."));
        }
    }

    private async Task HandleChannelGetAsync(IMessageContext<StatisticsChannelGetRequestMessage> context)
    {
        try
        {
            var channel = await WithQuery(query => query.GetChannelAsync(context.Message.CreatorSourceId));
            await context.RespondAsync(channel is null
                ? new StatisticsChannelGetResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Creator source '{context.Message.CreatorSourceId}' was not found."
                }
                : new StatisticsChannelGetResponseMessage
                {
                    Success = true,
                    Channel = channel
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling channel statistics query for {CreatorSourceId}.", context.Message.CreatorSourceId);
            await context.RespondAsync(Failure<StatisticsChannelGetResponseMessage>("internal_error", "Internal statistics service error."));
        }
    }

    private async Task HandleDownloadHistoryAsync(IMessageContext<StatisticsDownloadHistoryRequestMessage> context)
    {
        try
        {
            var buckets = await WithQuery(query => query.GetDownloadHistoryAsync(context.Message));
            await context.RespondAsync(new StatisticsDownloadHistoryResponseMessage
            {
                Success = true,
                Buckets = buckets
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            await context.RespondAsync(new StatisticsDownloadHistoryResponseMessage
            {
                Success = false,
                ErrorCode = "validation",
                ErrorMessage = ex.Message
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling download history statistics query.");
            await context.RespondAsync(Failure<StatisticsDownloadHistoryResponseMessage>("internal_error", "Internal statistics service error."));
        }
    }

    private async Task<T> WithQuery<T>(Func<IStatisticsReadService, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStatisticsReadService>();
        return await action(service);
    }

    private static T Failure<T>(string errorCode, string errorMessage)
        where T : class
        => typeof(T) switch
        {
            var t when t == typeof(StatisticsOverviewResponseMessage) =>
                (new StatisticsOverviewResponseMessage { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage } as T)!,
            var t when t == typeof(StatisticsChannelsListResponseMessage) =>
                (new StatisticsChannelsListResponseMessage { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage } as T)!,
            var t when t == typeof(StatisticsChannelGetResponseMessage) =>
                (new StatisticsChannelGetResponseMessage { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage } as T)!,
            var t when t == typeof(StatisticsDownloadHistoryResponseMessage) =>
                (new StatisticsDownloadHistoryResponseMessage { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage } as T)!,
            _ => throw new InvalidOperationException($"Unsupported response type {typeof(T).Name}.")
        };
}
