using System.Text.Json.Serialization;
using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

/// <summary>
/// Serves the unified <c>/api/search</c> surface. <see cref="SearchSubjects.Query"/> runs advanced
/// search across media metadata, subtitle text, and comment text (parent media surfaced and
/// de-duplicated), and <see cref="SearchSubjects.Similar"/> returns content-based "more like this".
/// Media metadata hits are the primary, paginated result set; subtitle/comment/note matches are
/// appended (they are not globally re-ranked across pages).
/// </summary>
public sealed class UnifiedSearchConsumerService(
    IMessageBus messageBus,
    ITypesenseClient typesense,
    IServiceScopeFactory scopeFactory,
    ILogger<UnifiedSearchConsumerService> logger) : SubscriptionBackgroundService
{
    private const string MediaQueryBy = "title,description,account_name,tags,categories,genres,artists";
    private const string MediaQueryByWeights = "4,2,2,1,1,1,1";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<SearchQueryRequestMessage>(
            messageBus,
            SearchSubjects.Query,
            HandleQueryAsync,
            queueGroup: SearchSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<SearchSimilarRequestMessage>(
            messageBus,
            SearchSubjects.Similar,
            HandleSimilarAsync,
            queueGroup: SearchSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to unified search subjects.");
    }

    private async Task HandleQueryAsync(IMessageContext<SearchQueryRequestMessage> context)
    {
        var request = context.Message;
        var page = TypesenseSearchHelpers.NormalizePage(request.Page);
        var pageSize = TypesenseSearchHelpers.NormalizePageSize(request.PageSize, defaultValue: 24);
        var parsed = AdvancedQueryParser.Parse(request.Query);

        if (!parsed.HasFreeText && !parsed.HasFilters)
        {
            await context.RespondAsync(new SearchQueryResponseMessage
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
            var collector = new HitCollector();

            // 1. Primary, paginated media-metadata search.
            var mediaParameters = new SearchParameters(parsed.EffectiveQuery, MediaQueryBy)
            {
                QueryByWeights = MediaQueryByWeights,
                FilterBy = parsed.FilterBy,
                Page = page,
                PerPage = pageSize,
                IncludeFields = TypesenseSearchHelpers.MediaCardIncludeFields
            };

            if (!string.IsNullOrWhiteSpace(request.SortBy))
            {
                mediaParameters.SortBy =
                    $"{TypesenseSearchHelpers.MapMediaSortField(request.SortBy)}:{TypesenseSearchHelpers.NormalizeSortOrder(request.SortOrder)}";
            }

            var mediaResult = await typesense.Search<MediaDocument>(MediaCollectionSchema.CollectionName, mediaParameters);
            foreach (var hit in mediaResult.Hits)
                collector.Add(hit.Document, SearchMatch.Metadata);

            // 2. Cross-content augmentation: surface parent media that matched in subtitles/comments.
            var appended = 0;
            if (parsed.HasFreeText)
                appended = await AugmentWithContentMatchesAsync(request, parsed, pageSize, collector);

            // 3. Private user-note matches (parity with metadata.search).
            if (parsed.HasFreeText)
                appended += await AugmentWithNoteMatchesAsync(request, pageSize, collector);

            var items = await ToHitsAsync(collector, request.OwnerSubject);
            await context.RespondAsync(new SearchQueryResponseMessage
            {
                Success = true,
                Items = items,
                Page = page,
                TotalCount = mediaResult.Found + appended,
                HasMore = page * pageSize < mediaResult.Found
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling unified search query.");
            await context.RespondAsync(new SearchQueryResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal search service error.",
                Page = page
            });
        }
    }

    private async Task HandleSimilarAsync(IMessageContext<SearchSimilarRequestMessage> context)
    {
        var request = context.Message;
        var pageSize = TypesenseSearchHelpers.NormalizePageSize(request.PageSize, defaultValue: 12);

        try
        {
            var source = await scopeFactory.WithScopedAsync<IMediaDocumentQuery, MediaDocument?>(
                query => query.GetMediaByGuidAsync(request.MediaGuid));

            if (source is null)
            {
                await context.RespondAsync(new SearchSimilarResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = "Media item was not found."
                });
                return;
            }

            var terms = source.Tags
                .Concat(source.Genres)
                .Concat(source.Categories)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var queryText = terms.Count > 0 ? string.Join(' ', terms) : source.Title;
            if (string.IsNullOrWhiteSpace(queryText))
            {
                await context.RespondAsync(new SearchSimilarResponseMessage { Success = true, Items = [] });
                return;
            }

            var parameters = new SearchParameters(queryText, "tags,categories,genres,title,account_name")
            {
                QueryByWeights = "4,3,3,2,1",
                FilterBy = TypesenseSearchHelpers.Ne("id", source.Id),
                Page = 1,
                PerPage = pageSize,
                IncludeFields = TypesenseSearchHelpers.MediaCardIncludeFields
            };

            var result = await typesense.Search<MediaDocument>(MediaCollectionSchema.CollectionName, parameters);

            var collector = new HitCollector();
            foreach (var hit in result.Hits)
                collector.Add(hit.Document, SearchMatch.Similar);

            var items = await ToHitsAsync(collector, request.OwnerSubject);
            await context.RespondAsync(new SearchSimilarResponseMessage { Success = true, Items = items });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling similar-media query for {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new SearchSimilarResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal search service error."
            });
        }
    }

    private async Task<int> AugmentWithContentMatchesAsync(
        SearchQueryRequestMessage request,
        ParsedQuery parsed,
        int pageSize,
        HitCollector collector)
    {
        var wantSubtitles = ScopeIncludes(request.Scope, SearchScope.Subtitles);
        var wantComments = ScopeIncludes(request.Scope, SearchScope.Comments);
        if (!wantSubtitles && !wantComments)
            return 0;

        // guid -> reasons it matched in content collections.
        var contentReasons = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        if (wantSubtitles)
        {
            foreach (var guid in await SearchContentGuidsAsync(CaptionsCollectionSchema.CollectionName, parsed.FreeText, pageSize))
                AddReason(contentReasons, guid, SearchMatch.Subtitles);
        }

        if (wantComments)
        {
            foreach (var guid in await SearchContentGuidsAsync(CommentsCollectionSchema.CollectionName, parsed.FreeText, pageSize))
                AddReason(contentReasons, guid, SearchMatch.Comments);
        }

        if (contentReasons.Count == 0)
            return 0;

        // Merge reasons into media hits we already have; collect the rest to hydrate.
        var toHydrate = new List<string>();
        foreach (var (guid, reasons) in contentReasons)
        {
            if (collector.Contains(guid))
            {
                foreach (var reason in reasons)
                    collector.AddReason(guid, reason);
            }
            else
            {
                toHydrate.Add(guid);
            }
        }

        if (toHydrate.Count == 0)
            return 0;

        // Hydrate via the media collection so the same structured filters still apply.
        var hydrated = await HydrateMediaByIdsAsync(toHydrate, parsed.FilterBy, pageSize);
        var appended = 0;
        foreach (var document in hydrated)
        {
            foreach (var reason in contentReasons[document.Id])
                collector.Add(document, reason);
            appended++;
        }

        return appended;
    }

    private async Task<int> AugmentWithNoteMatchesAsync(
        SearchQueryRequestMessage request,
        int pageSize,
        HitCollector collector)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerSubject) || !ScopeIncludes(request.Scope, SearchScope.Metadata))
            return 0;

        var noteGuids = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyList<Guid>>(
            notes => notes.SearchVideoNoteTargetsAsync(request.OwnerSubject, request.Query, pageSize));

        var missing = noteGuids
            .Where(guid => !collector.Contains(TypesenseSearchHelpers.NormalizeGuid(guid)))
            .Distinct()
            .ToArray();
        if (missing.Length == 0)
            return 0;

        var documents = await scopeFactory.WithScopedAsync<IMediaDocumentQuery, IReadOnlyList<MediaDocument>>(
            query => query.GetMediaByGuidsAsync(missing));

        var appended = 0;
        foreach (var document in documents)
        {
            collector.Add(document, SearchMatch.Notes);
            appended++;
        }

        return appended;
    }

    private async Task<IReadOnlyList<string>> SearchContentGuidsAsync(string collection, string query, int limit)
    {
        var parameters = new SearchParameters(query, "text")
        {
            Page = 1,
            PerPage = Math.Clamp(limit, 1, 250),
            IncludeFields = "media_guid"
        };

        var result = await typesense.Search<MediaGuidHit>(collection, parameters);
        return result.Hits
            .Select(x => x.Document.MediaGuid)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyList<MediaDocument>> HydrateMediaByIdsAsync(
        IReadOnlyList<string> ids,
        string? baseFilter,
        int pageSize)
    {
        var capped = ids.Take(TypesenseSearchHelpers.MaxPageSize).ToArray();
        var idFilter = "(" + string.Join(" || ", capped.Select(id => TypesenseSearchHelpers.Eq("id", id))) + ")";
        var filter = string.IsNullOrWhiteSpace(baseFilter) ? idFilter : $"{baseFilter} && {idFilter}";

        var parameters = new SearchParameters("*", "title")
        {
            FilterBy = filter,
            Page = 1,
            PerPage = Math.Clamp(capped.Length, 1, 250),
            IncludeFields = TypesenseSearchHelpers.MediaCardIncludeFields
        };

        var result = await typesense.Search<MediaDocument>(MediaCollectionSchema.CollectionName, parameters);
        return result.Hits.Select(x => x.Document).ToList();
    }

    private async Task<IReadOnlyList<SearchHitDto>> ToHitsAsync(HitCollector collector, string? ownerSubject)
    {
        var documents = collector.Documents;
        if (documents.Count == 0)
            return [];

        IReadOnlyDictionary<Guid, string> videoNotes = new Dictionary<Guid, string>();
        IReadOnlyDictionary<long, string> channelNotes = new Dictionary<long, string>();

        if (!string.IsNullOrWhiteSpace(ownerSubject))
        {
            var mediaGuids = documents.Select(x => Guid.ParseExact(x.Id, "N")).ToArray();
            var accountIds = documents.Select(x => x.AccountId).ToArray();

            videoNotes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<Guid, string>>(
                repository => repository.GetVideoNotesAsync(ownerSubject, mediaGuids));
            channelNotes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<long, string>>(
                repository => repository.GetChannelNotesAsync(ownerSubject, accountIds));
        }

        return documents
            .Select(document =>
            {
                var mediaGuid = Guid.ParseExact(document.Id, "N");
                var card = TypesenseSearchHelpers.ToCardDto(
                    document,
                    videoNotes.GetValueOrDefault(mediaGuid),
                    channelNotes.GetValueOrDefault(document.AccountId));
                return new SearchHitDto
                {
                    Media = card,
                    MatchedIn = collector.ReasonsFor(document.Id)
                };
            })
            .ToArray();
    }

    private static bool ScopeIncludes(string? scope, string surface)
        => string.IsNullOrWhiteSpace(scope)
            || scope.Equals(SearchScope.All, StringComparison.OrdinalIgnoreCase)
            || scope.Equals(surface, StringComparison.OrdinalIgnoreCase);

    private static void AddReason(Dictionary<string, SortedSet<string>> map, string guid, string reason)
    {
        if (!map.TryGetValue(guid, out var set))
        {
            set = new SortedSet<string>(StringComparer.Ordinal);
            map[guid] = set;
        }

        set.Add(reason);
    }

    /// <summary>Accumulates result documents in insertion order with their de-duplicated match reasons.</summary>
    private sealed class HitCollector
    {
        private readonly List<MediaDocument> _documents = [];
        private readonly Dictionary<string, SortedSet<string>> _reasons = new(StringComparer.Ordinal);

        public IReadOnlyList<MediaDocument> Documents => _documents;

        public bool Contains(string id) => _reasons.ContainsKey(id);

        public void Add(MediaDocument document, string reason)
        {
            if (!_reasons.TryGetValue(document.Id, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                _reasons[document.Id] = set;
                _documents.Add(document);
            }

            set.Add(reason);
        }

        public void AddReason(string id, string reason)
        {
            if (_reasons.TryGetValue(id, out var set))
                set.Add(reason);
        }

        public IReadOnlyList<string> ReasonsFor(string id)
            => _reasons.TryGetValue(id, out var set) ? set.ToArray() : [];
    }

    private sealed record MediaGuidHit
    {
        [JsonPropertyName("media_guid")]
        public string MediaGuid { get; init; } = string.Empty;
    }
}
