using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

public sealed class MetadataCaptionsConsumerService(
    IMessageBus messageBus,
    ITypesenseClient typesense,
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
            var filters = new List<string>
            {
                TypesenseSearchHelpers.Eq("media_guid", TypesenseSearchHelpers.NormalizeGuid(request.MediaGuid))
            };

            if (!string.IsNullOrWhiteSpace(request.LanguageCode))
                filters.Add(TypesenseSearchHelpers.Eq("language_code", request.LanguageCode.Trim()));
            if (!string.IsNullOrWhiteSpace(request.CaptionType))
                filters.Add(TypesenseSearchHelpers.Eq("caption_type", request.CaptionType.Trim()));

            var parameters = new SearchParameters("*", "name")
            {
                FilterBy = string.Join(" && ", filters),
                Page = 1,
                PerPage = 250
            };

            var result = await typesense.Search<CaptionDocument>(
                CaptionsCollectionSchema.CollectionName,
                parameters);

            var items = result.Hits
                .Select(x => x.Document)
                .OrderBy(x => x.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.CaptionType, StringComparer.OrdinalIgnoreCase)
                .Select(TypesenseSearchHelpers.ToCaptionDto)
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
