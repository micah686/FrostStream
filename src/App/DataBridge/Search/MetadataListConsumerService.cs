using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

public sealed class MetadataListConsumerService(
    IMessageBus messageBus,
    ITypesenseClient typesense,
    ILogger<MetadataListConsumerService> logger) : BackgroundService
{
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataListRequestMessage>(
            MetadataSubjects.List,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataListRequestMessage>(
            MetadataSubjects.AccountsMediaList,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken));

        logger.LogInformation("Subscribed to metadata list subjects.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(IMessageContext<MetadataListRequestMessage> context)
    {
        var request = context.Message;
        var page = TypesenseSearchHelpers.NormalizePage(request.Page);
        var pageSize = TypesenseSearchHelpers.NormalizePageSize(request.PageSize, defaultValue: 24);

        try
        {
            var parameters = new SearchParameters("*", "title")
            {
                FilterBy = TypesenseSearchHelpers.BuildMediaFilter(
                    request.Platform,
                    request.AccountId,
                    request.Tag,
                    request.Category,
                    request.Genre,
                    request.CaptionLanguage),
                SortBy = $"{TypesenseSearchHelpers.MapMediaSortField(request.SortBy)}:{TypesenseSearchHelpers.NormalizeSortOrder(request.SortOrder)}",
                Page = page,
                PerPage = pageSize,
                IncludeFields = "id,title,thumbnail_storage_path,account_avatar_storage_path,duration_seconds,release_date_unix,view_count,availability,was_live,platform,account_id,account_name,account_handle"
            };

            var result = await typesense.Search<MediaDocument>(
                MediaCollectionSchema.CollectionName,
                parameters);

            var items = result.Hits.Select(x => TypesenseSearchHelpers.ToCardDto(x.Document)).ToArray();
            await context.RespondAsync(new MetadataListResponseMessage
            {
                Success = true,
                Items = items,
                Page = page,
                TotalCount = result.Found,
                HasMore = page * pageSize < result.Found
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling metadata list query.");
            await context.RespondAsync(new MetadataListResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata search service error.",
                Page = page
            });
        }
    }
}
