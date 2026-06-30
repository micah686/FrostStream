using DataBridge;
using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

public sealed class MetadataSearchConsumerService(
    IMessageBus messageBus,
    ITypesenseClient typesense,
    IServiceScopeFactory scopeFactory,
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

            var documents = result.Hits.Select(x => x.Document).ToList();
            var noteMatchedDocuments = await GetPrivateVideoNoteMatchesAsync(request, pageSize, documents);
            documents.AddRange(noteMatchedDocuments);

            var items = await ToCardsAsync(documents, request.OwnerSubject);
            await context.RespondAsync(new MetadataSearchResponseMessage
            {
                Success = true,
                Items = items,
                Page = page,
                TotalCount = result.Found + noteMatchedDocuments.Count,
                HasMore = page * pageSize < result.Found || noteMatchedDocuments.Count >= pageSize
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

    private async Task<IReadOnlyList<MediaDocument>> GetPrivateVideoNoteMatchesAsync(
        MetadataSearchRequestMessage request,
        int pageSize,
        IReadOnlyCollection<MediaDocument> existingDocuments)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerSubject))
            return [];

        var existingIds = existingDocuments
            .Select(x => Guid.ParseExact(x.Id, "N"))
            .ToHashSet();

        return await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyList<MediaDocument>>(async notes =>
        {
            var noteIds = await notes.SearchVideoNoteTargetsAsync(request.OwnerSubject, request.Query, pageSize);
            var missingIds = noteIds.Where(x => !existingIds.Contains(x)).ToArray();
            if (missingIds.Length == 0)
                return [];

            var documents = await scopeFactory.WithScopedAsync<IMediaDocumentQuery, IReadOnlyList<MediaDocument>>(
                query => query.GetMediaByGuidsAsync(missingIds));
            var byId = documents.ToDictionary(x => Guid.ParseExact(x.Id, "N"));
            return missingIds
                .Where(byId.ContainsKey)
                .Select(x => byId[x])
                .ToArray();
        });
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
