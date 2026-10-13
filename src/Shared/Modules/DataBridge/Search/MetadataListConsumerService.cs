using DataBridge;
using DataBridge.Data;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class MetadataListConsumerService(
    IMessageBus messageBus,
    ISearchReader search,
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
        var page = SearchHelpers.NormalizePage(request.Page);
        var pageSize = SearchHelpers.NormalizePageSize(request.PageSize, defaultValue: 24);

        try
        {
            var parameters = new SearchRequest("*", "title")
            {
                Filter = SearchHelpers.BuildMediaFilter(
                    request.Platform,
                    request.AccountId,
                    request.Tag,
                    request.Category,
                    request.Genre,
                    request.CaptionLanguage),
                SortBy = new SearchSort(SearchHelpers.MapMediaSortField(request.SortBy), SearchHelpers.NormalizeSortOrder(request.SortOrder) == "desc"),
                Page = page,
                PerPage = pageSize,
                IncludeFields = "id,title,thumbnail_storage_path,account_avatar_storage_path,duration_seconds,release_date_unix,view_count,availability,was_live,platform,account_id,account_name,account_handle"
            };

            var result = await search.SearchAsync<MediaDocument>(
                SearchCollection.Media,
                parameters);

            var documents = await RefreshMissingThumbnailPathsAsync(
                result.Hits.Select(x => x.Document).ToArray());
            var items = await ToCardsAsync(documents, request.OwnerSubject);
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

    private async Task<IReadOnlyList<MediaDocument>> RefreshMissingThumbnailPathsAsync(
        IReadOnlyList<MediaDocument> documents)
    {
        var missingThumbnailIds = documents
            .Where(x => string.IsNullOrWhiteSpace(x.ThumbnailStoragePath))
            .Select(x => Guid.ParseExact(x.Id, "N"))
            .ToArray();
        if (missingThumbnailIds.Length == 0)
            return documents;

        var currentDocuments = await scopeFactory.WithScopedAsync<IMediaDocumentQuery, IReadOnlyList<MediaDocument>>(
            query => query.GetMediaByGuidsAsync(missingThumbnailIds));
        var currentById = currentDocuments.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        return documents
            .Select(document =>
            {
                if (!currentById.TryGetValue(document.Id, out var current)
                    || string.IsNullOrWhiteSpace(current.ThumbnailStoragePath))
                {
                    return document;
                }

                return document with { ThumbnailStoragePath = current.ThumbnailStoragePath };
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<MetadataCardDto>> ToCardsAsync(
        IReadOnlyList<MediaDocument> documents,
        string? ownerSubject)
    {
        if (documents.Count == 0)
            return [];

        if (string.IsNullOrWhiteSpace(ownerSubject))
            return documents.Select(SearchHelpers.ToCardDto).ToArray();

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
                return SearchHelpers.ToCardDto(
                    x,
                    notes.GetValueOrDefault(mediaGuid),
                    channelNotes.GetValueOrDefault(x.AccountId));
            })
            .ToArray();
    }
}
