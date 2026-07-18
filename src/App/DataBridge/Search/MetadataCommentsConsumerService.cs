using DataBridge;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class MetadataCommentsConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
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
            var comments = await scopeFactory.WithScopedAsync<IMediaDocumentQuery, IReadOnlyList<CommentDocument>>(
                query => query.GetCommentsByMediaGuidAsync(request.MediaGuid));

            IEnumerable<CommentDocument> filtered = comments;

            var parentId = request.ParentCommentId?.Trim();
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                filtered = filtered.Where(comment => string.Equals(comment.ParentCommentId, parentId, StringComparison.Ordinal));
            }

            var queryText = request.Query?.Trim();
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                filtered = filtered.Where(comment =>
                    comment.Text.Contains(queryText, StringComparison.OrdinalIgnoreCase) ||
                    comment.AccountName.Contains(queryText, StringComparison.OrdinalIgnoreCase) ||
                    comment.AccountHandle.Contains(queryText, StringComparison.OrdinalIgnoreCase));
            }

            filtered = SortComments(filtered, request.SortBy, request.SortOrder);

            var totalCount = filtered.Count();
            var items = filtered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(TypesenseSearchHelpers.ToCommentDto)
                .ToArray();

            await context.RespondAsync(new MetadataCommentsListResponseMessage
            {
                Success = true,
                Items = items,
                Page = page,
                TotalCount = totalCount,
                HasMore = page * pageSize < totalCount
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

    private static IEnumerable<CommentDocument> SortComments(
        IEnumerable<CommentDocument> comments,
        string sortBy,
        string sortOrder)
    {
        var descending = string.Equals(TypesenseSearchHelpers.NormalizeSortOrder(sortOrder), "desc", StringComparison.OrdinalIgnoreCase);
        var sorted = TypesenseSearchHelpers.MapCommentSortField(sortBy) switch
        {
            "like_count" => descending
                ? comments.OrderByDescending(comment => comment.LikeCount ?? int.MinValue).ThenByDescending(comment => comment.CommentTimestampUnix).ThenBy(comment => comment.Id, StringComparer.Ordinal)
                : comments.OrderBy(comment => comment.LikeCount ?? int.MinValue).ThenBy(comment => comment.CommentTimestampUnix).ThenBy(comment => comment.Id, StringComparer.Ordinal),
            _ => descending
                ? comments.OrderByDescending(comment => comment.CommentTimestampUnix).ThenByDescending(comment => comment.Id, StringComparer.Ordinal)
                : comments.OrderBy(comment => comment.CommentTimestampUnix).ThenBy(comment => comment.Id, StringComparer.Ordinal)
        };

        return sorted;
    }
}
