using System.Security.Cryptography;
using System.Text;
using DataBridge;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class CreatorDiscoveryConsumerService(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<CreatorDiscoveryConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-creator-discovery";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<CreatorSourceCreateRequestMessage>(messageBus, CreatorDiscoverySubjects.CreateSource, HandleCreateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorSourceUpdateRequestMessage>(messageBus, CreatorDiscoverySubjects.UpdateSource, HandleUpdateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorSourceGetRequestMessage>(messageBus, CreatorDiscoverySubjects.GetSource, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorSourceListRequestMessage>(messageBus, CreatorDiscoverySubjects.ListSources, HandleListAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorSourceListEnabledForScanRequestMessage>(messageBus, CreatorDiscoverySubjects.ListEnabledSourcesForScan, HandleListEnabledForScanAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorSourceDeleteRequestMessage>(messageBus, CreatorDiscoverySubjects.DeleteSource, HandleDeleteAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UpsertDiscoveredMediaBatchRequestMessage>(messageBus, CreatorDiscoverySubjects.UpsertDiscoveredMediaBatch, HandleUpsertBatchAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UpdateCreatorSourceAssetsRequestMessage>(messageBus, CreatorDiscoverySubjects.UpdateAssets, HandleUpdateAssetsAsync, QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to creator discovery subjects.");
    }

    private async Task HandleCreateAsync(IMessageContext<CreatorSourceCreateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (Validate(msg.Platform, msg.SourceUrl, msg.IncrementalPageSize, msg.ConsecutiveKnownThreshold, msg.FullRescanIntervalDays, msg.MetadataRefreshWindow) is { } validationError)
            {
                await context.RespondAsync(Failure(validationError));
                return;
            }

            var entity = await WithRepo(repo => repo.CreateSourceAsync(new CreatorSourceEntity
            {
                Platform = msg.Platform.Trim(),
                SourceType = msg.SourceType,
                SourceUrl = msg.SourceUrl.Trim(),
                ScanEnabled = msg.ScanEnabled,
                IncrementalPageSize = msg.IncrementalPageSize,
                ConsecutiveKnownThreshold = msg.ConsecutiveKnownThreshold,
                FullRescanIntervalDays = msg.FullRescanIntervalDays,
                MetadataRefreshWindow = msg.MetadataRefreshWindow
            }));
            await context.RespondAsync(new CreatorSourceOperationResponseMessage { Success = true, Entity = Map(entity) });
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Creator source create conflicted for URL {SourceUrl}", msg.SourceUrl);
            await context.RespondAsync(new CreatorSourceOperationResponseMessage
            {
                Success = false,
                ErrorCode = "conflict",
                ErrorMessage = $"Creator source URL '{msg.SourceUrl}' already exists."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed creating creator source {SourceUrl}", msg.SourceUrl);
            await context.RespondAsync(InternalFailure("Failed to create creator source."));
        }
    }

    private async Task HandleUpdateAsync(IMessageContext<CreatorSourceUpdateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (Validate(msg.Platform, msg.SourceUrl, msg.IncrementalPageSize, msg.ConsecutiveKnownThreshold, msg.FullRescanIntervalDays, msg.MetadataRefreshWindow) is { } validationError)
            {
                await context.RespondAsync(Failure(validationError));
                return;
            }

            var updated = await WithRepo(repo => repo.UpdateSourceAsync(new CreatorSourceEntity
            {
                Id = msg.Id,
                Platform = msg.Platform.Trim(),
                SourceType = msg.SourceType,
                SourceUrl = msg.SourceUrl.Trim(),
                ScanEnabled = msg.ScanEnabled,
                IncrementalPageSize = msg.IncrementalPageSize,
                ConsecutiveKnownThreshold = msg.ConsecutiveKnownThreshold,
                FullRescanIntervalDays = msg.FullRescanIntervalDays,
                MetadataRefreshWindow = msg.MetadataRefreshWindow
            }));
            if (updated is null)
            {
                await context.RespondAsync(NotFound(msg.Id));
                return;
            }

            await context.RespondAsync(new CreatorSourceOperationResponseMessage { Success = true, Entity = Map(updated) });
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Creator source update conflicted for URL {SourceUrl}", msg.SourceUrl);
            await context.RespondAsync(new CreatorSourceOperationResponseMessage
            {
                Success = false,
                ErrorCode = "conflict",
                ErrorMessage = $"Creator source URL '{msg.SourceUrl}' already exists."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating creator source {SourceId}", msg.Id);
            await context.RespondAsync(InternalFailure("Failed to update creator source."));
        }
    }

    private async Task HandleGetAsync(IMessageContext<CreatorSourceGetRequestMessage> context)
    {
        var id = context.Message.Id;
        try
        {
            var entity = await WithRepo(repo => repo.GetSourceAsync(id));
            await context.RespondAsync(entity is null
                ? NotFound(id)
                : new CreatorSourceOperationResponseMessage { Success = true, Entity = Map(entity) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting creator source {SourceId}", id);
            await context.RespondAsync(InternalFailure("Failed to get creator source."));
        }
    }

    private async Task HandleListAsync(IMessageContext<CreatorSourceListRequestMessage> context)
    {
        try
        {
            var items = await WithRepo(repo => repo.ListSourcesAsync());
            await context.RespondAsync(new CreatorSourceOperationResponseMessage
            {
                Success = true,
                Items = items.Select(Map).ToArray()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing creator sources.");
            await context.RespondAsync(InternalFailure("Failed to list creator sources."));
        }
    }

    private async Task HandleListEnabledForScanAsync(IMessageContext<CreatorSourceListEnabledForScanRequestMessage> context)
    {
        try
        {
            var items = await WithRepo(repo => repo.ListEnabledSourcesForScanAsync(context.Message.ScanMode));
            await context.RespondAsync(new CreatorSourceOperationResponseMessage
            {
                Success = true,
                Items = items.Select(Map).ToArray()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing enabled creator sources.");
            await context.RespondAsync(InternalFailure("Failed to list enabled creator sources."));
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<CreatorSourceDeleteRequestMessage> context)
    {
        var id = context.Message.Id;
        try
        {
            var deleted = await WithRepo(repo => repo.DeleteSourceAsync(id));
            await context.RespondAsync(deleted
                ? new CreatorSourceOperationResponseMessage { Success = true }
                : NotFound(id));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting creator source {SourceId}", id);
            await context.RespondAsync(InternalFailure("Failed to delete creator source."));
        }
    }

    private async Task HandleUpsertBatchAsync(IMessageContext<UpsertDiscoveredMediaBatchRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            var result = await WithRepo(repo => repo.UpsertDiscoveredMediaBatchAsync(msg));

            foreach (var candidate in result.EnqueuedItems)
            {
                await PublishDownloadRequestedAsync(msg, candidate);
            }

            await context.RespondAsync(new UpsertDiscoveredMediaBatchResponseMessage
            {
                Success = true,
                TotalSeen = result.TotalSeen,
                NewCount = result.NewCount,
                ChangedCount = result.ChangedCount,
                EnqueuedItems = result.EnqueuedItems
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed upserting discovered media for source {CreatorSourceId}", msg.CreatorSourceId);
            await context.RespondAsync(new UpsertDiscoveredMediaBatchResponseMessage
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to upsert discovered media."
            });
        }
    }

    private async Task HandleUpdateAssetsAsync(IMessageContext<UpdateCreatorSourceAssetsRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            var updated = await WithRepo(repo => repo.UpdateAssetsAsync(msg));
            if (updated is null)
            {
                await context.RespondAsync(new UpdateCreatorSourceAssetsResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Creator source '{msg.SourceId}' was not found."
                });
                return;
            }

            await context.RespondAsync(new UpdateCreatorSourceAssetsResponseMessage
            {
                Success = true,
                Entity = Map(updated)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating creator source assets {SourceId}", msg.SourceId);
            await context.RespondAsync(new UpdateCreatorSourceAssetsResponseMessage
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to update creator source assets."
            });
        }
    }

    private Task PublishDownloadRequestedAsync(UpsertDiscoveredMediaBatchRequestMessage request, DiscoveredMediaCandidate candidate)
    {
        var seed = $"{request.CreatorSourceId}:{candidate.Platform}:{candidate.Extractor}:{candidate.ExternalMediaId}";
        var jobId = DeterministicGuid(seed);
        var messageId = DeterministicGuid($"{seed}:download-requested");
        var operationKey = $"creator-discovery/{request.CreatorSourceId}/{candidate.Platform}/{candidate.Extractor}/{candidate.ExternalMediaId}";
        return publisher.PublishAsync(
            DownloadSubjects.DownloadRequested,
            new DownloadRequested
            {
                JobId = jobId,
                CorrelationId = jobId,
                CausationId = null,
                MessageId = messageId,
                OperationKey = operationKey,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                SourceUrl = candidate.CanonicalUrl,
                RequestedBy = $"schedule:{request.ScheduleKey}",
                ForceDownload = false,
                MediaKind = MediaKind.Video
            },
            messageId: messageId.ToString("N"));
    }

    private Task<TResult> WithRepo<TResult>(Func<ICreatorDiscoveryRepository, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private static string? Validate(
        string platform,
        string sourceUrl,
        int incrementalPageSize,
        int consecutiveKnownThreshold,
        int fullRescanIntervalDays,
        int metadataRefreshWindow)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return "platform is required.";
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return "source_url is required.";
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out _))
            return "source_url must be an absolute URL.";
        if (incrementalPageSize <= 0)
            return "incremental_page_size must be greater than zero.";
        if (consecutiveKnownThreshold <= 0)
            return "consecutive_known_threshold must be greater than zero.";
        if (fullRescanIntervalDays <= 0)
            return "full_rescan_interval_days must be greater than zero.";
        if (metadataRefreshWindow <= 0)
            return "metadata_refresh_window must be greater than zero.";
        return null;
    }

    private static CreatorSourceOperationResponseMessage Failure(string message)
        => new() { Success = false, ErrorCode = "validation", ErrorMessage = message };

    private static CreatorSourceOperationResponseMessage NotFound(long id)
        => new() { Success = false, ErrorCode = "not_found", ErrorMessage = $"Creator source '{id}' was not found." };

    private static CreatorSourceOperationResponseMessage InternalFailure(string message)
        => new() { Success = false, ErrorCode = "internal", ErrorMessage = message };

    private static CreatorSourceDto Map(CreatorSourceEntity entity) => new()
    {
        Id = entity.Id,
        Platform = entity.Platform,
        SourceType = entity.SourceType,
        SourceUrl = entity.SourceUrl,
        ScanEnabled = entity.ScanEnabled,
        IncrementalPageSize = entity.IncrementalPageSize,
        ConsecutiveKnownThreshold = entity.ConsecutiveKnownThreshold,
        FullRescanIntervalDays = entity.FullRescanIntervalDays,
        MetadataRefreshWindow = entity.MetadataRefreshWindow,
        LastSuccessfulScanAt = entity.LastSuccessfulScanAt,
        LastFullScanAt = entity.LastFullScanAt,
        LastSeenHighWatermark = entity.LastSeenHighWatermark,
        CreatedAt = entity.CreatedAt,
        LastUpdated = entity.LastUpdated,
        AvatarUrl = entity.AvatarUrl,
        AvatarCachePath = entity.AvatarCachePath,
        AvatarContentHash = entity.AvatarContentHash,
        BannerUrl = entity.BannerUrl,
        BannerCachePath = entity.BannerCachePath,
        BannerContentHash = entity.BannerContentHash,
        AssetsLastRefreshedAt = entity.AssetsLastRefreshedAt,
        AssetsLastAttemptAt = entity.AssetsLastAttemptAt,
        AssetsAttemptCount = entity.AssetsAttemptCount,
        AssetsLastError = entity.AssetsLastError
    };

    private static Guid DeterministicGuid(string seed)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(bytes);
    }
}
