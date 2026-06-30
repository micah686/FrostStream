using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

public sealed class MetadataCommentsConsumerService(
    IMessageBus messageBus,
    ITypesenseClient typesense,
    ILogger<MetadataCommentsConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MetadataCommentsListRequestMessage>(
            messageBus,
            MetadataSubjects.CommentsList,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata comments subject.");
    }

    private async Task HandleAsync(IMessageContext<MetadataCommentsListRequestMessage> context)
    {
        var request = context.Message;
        var page = TypesenseSearchHelpers.NormalizePage(request.Page);
        var pageSize = TypesenseSearchHelpers.NormalizePageSize(request.PageSize, defaultValue: 20);

        try
        {
            var filters = new List<string>
            {
                TypesenseSearchHelpers.Eq("media_guid", TypesenseSearchHelpers.NormalizeGuid(request.MediaGuid)),
                TypesenseSearchHelpers.Eq("parent_comment_id", request.ParentCommentId?.Trim() ?? string.Empty)
            };

            var hasQuery = !string.IsNullOrWhiteSpace(request.Query);
            var parameters = new SearchParameters(hasQuery ? request.Query!.Trim() : "*", hasQuery ? "text,account_name" : "text")
            {
                FilterBy = string.Join(" && ", filters),
                SortBy = $"{TypesenseSearchHelpers.MapCommentSortField(request.SortBy)}:{TypesenseSearchHelpers.NormalizeSortOrder(request.SortOrder)}",
                Page = page,
                PerPage = pageSize
            };

            var result = await typesense.Search<CommentDocument>(
                CommentsCollectionSchema.CollectionName,
                parameters);

            var items = result.Hits.Select(x => TypesenseSearchHelpers.ToCommentDto(x.Document)).ToArray();
            await context.RespondAsync(new MetadataCommentsListResponseMessage
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
            logger.LogError(ex, "Failed handling metadata comments query for {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new MetadataCommentsListResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata search service error.",
                Page = page
            });
        }
    }
}
