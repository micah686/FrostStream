using DataBridge;
using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

public sealed class MetadataListConsumerService(
    IMessageBus messageBus,
    ITypesenseClient typesense,
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataListConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MetadataListRequestMessage>(
            messageBus,
            MetadataSubjects.List,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MetadataListRequestMessage>(
            messageBus,
            MetadataSubjects.AccountsMediaList,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata list subjects.");
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

            var items = await ToCardsAsync(result.Hits.Select(x => x.Document).ToArray(), request.OwnerSubject);
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

    private async Task<IReadOnlyList<MetadataCardDto>> ToCardsAsync(
        IReadOnlyList<MediaDocument> documents,
        string? ownerSubject)
    {
        if (documents.Count == 0)
            return [];

        if (string.IsNullOrWhiteSpace(ownerSubject))
            return documents.Select(TypesenseSearchHelpers.ToCardDto).ToArray();

        var mediaGuids = documents
            .Select(x => Guid.ParseExact(x.Id, "N"))
            .ToArray();
        var notes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<Guid, string>>(
            repository => repository.GetVideoNotesAsync(ownerSubject, mediaGuids));
        var channelNotes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<long, string>>(
            repository => repository.GetChannelNotesAsync(ownerSubject, documents.Select(x => x.AccountId).ToArray()));

        return documents
            .Select(x =>
            {
                var mediaGuid = Guid.ParseExact(x.Id, "N");
                return TypesenseSearchHelpers.ToCardDto(
                    x,
                    notes.GetValueOrDefault(mediaGuid),
                    channelNotes.GetValueOrDefault(x.AccountId));
            })
            .ToArray();
    }
}
