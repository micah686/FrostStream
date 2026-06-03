using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

public sealed class MetadataSearchConsumerService(
    IMessageBus messageBus,
    ITypesenseClient typesense,
    ILogger<MetadataSearchConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MetadataSearchRequestMessage>(
            messageBus,
            MetadataSubjects.Search,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata search subject.");
    }

    private async Task HandleAsync(IMessageContext<MetadataSearchRequestMessage> context)
    {
        var request = context.Message;
        var page = TypesenseSearchHelpers.NormalizePage(request.Page);
        var pageSize = TypesenseSearchHelpers.NormalizePageSize(request.PageSize, defaultValue: 24);

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            await context.RespondAsync(new MetadataSearchResponseMessage
            {
                Success = false,
                ErrorCode = "validation",
                ErrorMessage = "Search query is required.",
                Page = page
            });
            return;
        }

        try
        {
            var parameters = new SearchParameters(
                request.Query.Trim(),
                "title,description,account_name,tags,categories,genres,artists")
            {
                QueryByWeights = "4,2,2,1,1,1,1",
                FilterBy = TypesenseSearchHelpers.BuildMediaFilter(
                    request.Platform,
                    accountId: null,
                    request.Tag,
                    request.Category,
                    request.Genre,
                    captionLanguage: null),
                Page = page,
                PerPage = pageSize,
                IncludeFields = "id,title,thumbnail_storage_path,account_avatar_storage_path,duration_seconds,release_date_unix,view_count,availability,was_live,platform,account_id,account_name,account_handle"
            };

            if (!string.IsNullOrWhiteSpace(request.SortBy))
            {
                parameters.SortBy = $"{TypesenseSearchHelpers.MapMediaSortField(request.SortBy)}:{TypesenseSearchHelpers.NormalizeSortOrder(request.SortOrder)}";
            }

            var result = await typesense.Search<MediaDocument>(
                MediaCollectionSchema.CollectionName,
                parameters);

            var items = result.Hits.Select(x => TypesenseSearchHelpers.ToCardDto(x.Document)).ToArray();
            await context.RespondAsync(new MetadataSearchResponseMessage
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
            logger.LogError(ex, "Failed handling metadata search query.");
            await context.RespondAsync(new MetadataSearchResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata search service error.",
                Page = page
            });
        }
    }
}
