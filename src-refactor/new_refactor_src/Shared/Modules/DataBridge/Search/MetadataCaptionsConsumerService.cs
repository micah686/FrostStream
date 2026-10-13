using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class MetadataCaptionsConsumerService(
    IMessageBus messageBus,
    ISearchReader search,
    ILogger<MetadataCaptionsConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MetadataCaptionsListRequestMessage>(
            messageBus,
            MetadataSubjects.CaptionsList,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata captions subject.");
    }

    private async Task HandleAsync(IMessageContext<MetadataCaptionsListRequestMessage> context)
    {
        var request = context.Message;

        try
        {
            var filters = new List<SearchFilter>
            {
                SearchHelpers.Eq("media_guid", SearchHelpers.NormalizeGuid(request.MediaGuid))
            };

            if (!string.IsNullOrWhiteSpace(request.LanguageCode))
                filters.Add(SearchHelpers.Eq("language_code", request.LanguageCode.Trim()));
            if (!string.IsNullOrWhiteSpace(request.CaptionType))
                filters.Add(SearchHelpers.Eq("caption_type", request.CaptionType.Trim()));

            var parameters = new SearchRequest("*", "name")
            {
                Filter = new SearchFilter.All(filters),
                Page = 1,
                PerPage = 250
            };

            var result = await search.SearchAsync<CaptionDocument>(
                SearchCollection.Captions,
                parameters);

            var items = result.Hits
                .Select(x => x.Document)
                .OrderBy(x => x.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.CaptionType, StringComparer.OrdinalIgnoreCase)
                .Select(SearchHelpers.ToCaptionDto)
                .ToArray();

            await context.RespondAsync(new MetadataCaptionsListResponseMessage
            {
                Success = true,
                Items = items,
                TotalCount = result.Found
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling metadata captions query for {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new MetadataCaptionsListResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata search service error."
            });
        }
    }
}
