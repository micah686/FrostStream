using System.Security.Cryptography;
using System.Text;
using DataBridge;
using DataBridge.Data;
using Conduit.NATS;
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
    private const int MaxProviderQueryLimit = 5_000;

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<CreatorMonitorCreateRequestMessage>(messageBus, CreatorMonitorSubjects.CreateSource, HandleCreateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorMonitorCreateOrReuseRequestMessage>(messageBus, CreatorMonitorSubjects.CreateOrReuseSource, HandleCreateOrReuseAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorMonitorUpdateRequestMessage>(messageBus, CreatorMonitorSubjects.UpdateSource, HandleUpdateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorMonitorGetRequestMessage>(messageBus, CreatorMonitorSubjects.GetSource, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorMonitorListRequestMessage>(messageBus, CreatorMonitorSubjects.ListSources, HandleListAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorMonitorListEnabledForScanRequestMessage>(messageBus, CreatorMonitorSubjects.ListEnabledSourcesForScan, HandleListEnabledForScanAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CreatorMonitorDeleteRequestMessage>(messageBus, CreatorMonitorSubjects.DeleteSource, HandleDeleteAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UpsertDiscoveredMediaBatchRequestMessage>(messageBus, CreatorMonitorSubjects.UpsertDiscoveredMediaBatch, HandleUpsertBatchAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UpdateCreatorMonitorAssetsRequestMessage>(messageBus, CreatorMonitorSubjects.UpdateAssets, HandleUpdateAssetsAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ListIgnoredMediaRequestMessage>(messageBus, CreatorMonitorSubjects.ListIgnoredMedia, HandleListIgnoredMediaAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ForceQueueDiscoveredMediaRequestMessage>(messageBus, CreatorMonitorSubjects.ForceQueueDiscoveredMedia, HandleForceQueueDiscoveredMediaAsync, QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to creator discovery subjects.");
    }

    private async Task HandleCreateAsync(IMessageContext<CreatorMonitorCreateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (Validate(msg.Platform, msg.SourceUrl, msg.IncrementalPageSize, msg.ConsecutiveKnownThreshold, msg.FullRescanIntervalDays, msg.UpdateCheckIntervalHours, msg.MetadataRefreshWindow, msg.ProviderQueryLimits) is { } validationError)
            {
                await context.RespondAsync(Failure(validationError));
                return;
            }

            var entity = await WithRepo(repo => repo.CreateSourceAsync(new CreatorSourceEntity
            {
                Platform = msg.Platform.Trim(),
                SourceType = msg.SourceType,
                SourceUrl = Shared.Downloads.SourceUrlCanonicalizer.Canonicalize(msg.SourceUrl),
                ScanEnabled = msg.ScanEnabled,
                IncrementalPageSize = msg.IncrementalPageSize,
                ConsecutiveKnownThreshold = msg.ConsecutiveKnownThreshold,
                FullRescanIntervalDays = msg.FullRescanIntervalDays,
                UpdateCheckIntervalHours = msg.UpdateCheckIntervalHours,
                MetadataRefreshWindow = msg.MetadataRefreshWindow,
                ProviderQueryLimitsJson = msg.ProviderQueryLimits?.ToJson()
            }));
            await QueueInitialMetadataRefreshAsync(entity.Source.Id, CancellationToken.None);
            await context.RespondAsync(new CreatorMonitorOperationResponseMessage { Success = true, Entity = Map(entity) });
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Creator source create conflicted for URL {SourceUrl}", msg.SourceUrl);
            await context.RespondAsync(new CreatorMonitorOperationResponseMessage
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

    private async Task HandleCreateOrReuseAsync(IMessageContext<CreatorMonitorCreateOrReuseRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (Validate(msg.Platform, msg.SourceUrl, msg.IncrementalPageSize, msg.ConsecutiveKnownThreshold, msg.FullRescanIntervalDays, msg.UpdateCheckIntervalHours, msg.MetadataRefreshWindow, msg.ProviderQueryLimits) is { } validationError)
            {
                await context.RespondAsync(Failure(validationError));
                return;
            }

            var entity = await WithRepo(repo => repo.CreateOrReuseSourceAsync(new CreatorSourceEntity
            {
                Platform = msg.Platform.Trim(),
                SourceType = msg.SourceType,
                SourceUrl = Shared.Downloads.SourceUrlCanonicalizer.Canonicalize(msg.SourceUrl),
                ScanEnabled = msg.ScanEnabled,
                IncrementalPageSize = msg.IncrementalPageSize,
                ConsecutiveKnownThreshold = msg.ConsecutiveKnownThreshold,
                FullRescanIntervalDays = msg.FullRescanIntervalDays,
                UpdateCheckIntervalHours = msg.UpdateCheckIntervalHours,
                MetadataRefreshWindow = msg.MetadataRefreshWindow,
                ProviderQueryLimitsJson = msg.ProviderQueryLimits?.ToJson()
            }));
            if (entity.Source.AccountId is null)
                await QueueInitialMetadataRefreshAsync(entity.Source.Id, CancellationToken.None);
            await context.RespondAsync(new CreatorMonitorOperationResponseMessage { Success = true, Entity = Map(entity) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed creating or reusing creator source {SourceUrl}", msg.SourceUrl);
            await context.RespondAsync(InternalFailure("Failed to create or reuse creator source."));
        }
    }

    private async Task HandleUpdateAsync(IMessageContext<CreatorMonitorUpdateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (Validate(msg.Platform, msg.SourceUrl, msg.IncrementalPageSize, msg.ConsecutiveKnownThreshold, msg.FullRescanIntervalDays, msg.UpdateCheckIntervalHours, msg.MetadataRefreshWindow, msg.ProviderQueryLimits) is { } validationError)
            {
                await context.RespondAsync(Failure(validationError));
                return;
            }

            var updated = await WithRepo(repo => repo.UpdateSourceAsync(new CreatorSourceEntity
            {
                Id = msg.Id,
                Platform = msg.Platform.Trim(),
                SourceType = msg.SourceType,
                SourceUrl = Shared.Downloads.SourceUrlCanonicalizer.Canonicalize(msg.SourceUrl),
                ScanEnabled = msg.ScanEnabled,
                IncrementalPageSize = msg.IncrementalPageSize,
                ConsecutiveKnownThreshold = msg.ConsecutiveKnownThreshold,
                FullRescanIntervalDays = msg.FullRescanIntervalDays,
                UpdateCheckIntervalHours = msg.UpdateCheckIntervalHours,
                MetadataRefreshWindow = msg.MetadataRefreshWindow,
                ProviderQueryLimitsJson = msg.ProviderQueryLimits?.ToJson()
            }));
            if (updated is null)
            {
                await context.RespondAsync(NotFound(msg.Id));
                return;
            }

            await context.RespondAsync(new CreatorMonitorOperationResponseMessage { Success = true, Entity = Map(updated) });
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Creator source update conflicted for URL {SourceUrl}", msg.SourceUrl);
            await context.RespondAsync(new CreatorMonitorOperationResponseMessage
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

    private async Task HandleGetAsync(IMessageContext<CreatorMonitorGetRequestMessage> context)
    {
        var id = context.Message.Id;
        try
        {
            var entity = await WithRepo(repo => repo.GetSourceAsync(id));
            await context.RespondAsync(entity is null
                ? NotFound(id)
                : new CreatorMonitorOperationResponseMessage { Success = true, Entity = Map(entity) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting creator source {SourceId}", id);
            await context.RespondAsync(InternalFailure("Failed to get creator source."));
        }
    }

    private async Task HandleListAsync(IMessageContext<CreatorMonitorListRequestMessage> context)
    {
        try
        {
            var items = await WithRepo(repo => repo.ListSourcesAsync());
            await context.RespondAsync(new CreatorMonitorOperationResponseMessage
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

    private async Task HandleListEnabledForScanAsync(IMessageContext<CreatorMonitorListEnabledForScanRequestMessage> context)
    {
        try
        {
            var items = await WithRepo(repo => repo.ListEnabledSourcesForScanAsync(context.Message.ScanMode));
            await context.RespondAsync(new CreatorMonitorOperationResponseMessage
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

    private async Task HandleDeleteAsync(IMessageContext<CreatorMonitorDeleteRequestMessage> context)
    {
        var id = context.Message.Id;
        try
        {
            var deleted = await WithRepo(repo => repo.DeleteSourceAsync(id));
            await context.RespondAsync(deleted
                ? new CreatorMonitorOperationResponseMessage { Success = true }
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
            var canEnqueue = await scopeFactory.WithScopedAsync<IDownloadFlowV2Repository, bool>(
                repo => repo.CanAcceptGroupChildAsync(msg.CorrelationId));
            var effective = canEnqueue ? msg : msg with { SuppressDownloadEnqueue = true };
            var result = await WithRepo(repo => repo.UpsertDiscoveredMediaBatchAsync(effective));

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

    private async Task HandleUpdateAssetsAsync(IMessageContext<UpdateCreatorMonitorAssetsRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            var updated = await scopeFactory.WithScopedAsync<ICreatorDiscoveryRepository, IMetadataRepository, CreatorSourceRecord?>(
                async (creators, metadata) =>
                {
                    var entity = await creators.UpdateAssetsAsync(msg);
                    if (entity is null)
                        return null;

                    // Bridge the durable avatar/banner blobs into metadata.accounts (authoritative),
                    // then persist the resolved account id so the association is a real foreign key
                    // rather than a (platform, handle) string match repeated on every refresh.
                    if (!string.IsNullOrWhiteSpace(msg.Platform) &&
                        !string.IsNullOrWhiteSpace(msg.AccountHandle))
                    {
                        var accountId = await metadata.UpsertAccountAssetsAsync(
                            msg.Platform!,
                            msg.AccountHandle!,
                            string.IsNullOrWhiteSpace(msg.AccountName) ? "unknown" : msg.AccountName!,
                            msg.AccountUrl,
                            msg.AvatarStoragePath,
                            msg.BannerStoragePath,
                            string.IsNullOrWhiteSpace(msg.StorageKey) ? "default" : msg.StorageKey!);

                        await creators.LinkAccountAsync(entity.Source.Id, accountId);
                        entity.Source.AccountId = accountId;
                    }

                    return entity;
                });

            if (updated is null)
            {
                await context.RespondAsync(new UpdateCreatorMonitorAssetsResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Creator source '{msg.SourceId}' was not found."
                });
                return;
            }

            await context.RespondAsync(new UpdateCreatorMonitorAssetsResponseMessage
            {
                Success = true,
                Entity = Map(updated)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating creator source assets {SourceId}", msg.SourceId);
            await context.RespondAsync(new UpdateCreatorMonitorAssetsResponseMessage
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to update creator source assets."
            });
        }
    }

    private async Task HandleListIgnoredMediaAsync(IMessageContext<ListIgnoredMediaRequestMessage> context)
    {
        try
        {
            var items = await WithRepo(repo => repo.ListIgnoredMediaAsync(context.Message.CreatorSourceId));
            await context.RespondAsync(new ListIgnoredMediaResponseMessage
            {
                Success = true,
                Items = items.Select(x => new IgnoredMediaDto
                {
                    Id = x.Id,
                    CreatorSourceId = x.CreatorSourceId,
                    Title = x.Title,
                    CanonicalUrl = x.CanonicalUrl,
                    IgnoredKeyword = x.IgnoredKeyword,
                    FirstSeenAt = x.FirstSeenAt,
                    LastSeenAt = x.LastSeenAt
                }).ToArray()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing ignored media for source {SourceId}", context.Message.CreatorSourceId);
            await context.RespondAsync(new ListIgnoredMediaResponseMessage
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to list ignored media."
            });
        }
    }

    private async Task HandleForceQueueDiscoveredMediaAsync(IMessageContext<ForceQueueDiscoveredMediaRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            var entity = await WithRepo(repo => repo.RequeueIgnoredMediaAsync(msg.DiscoveredMediaId));
            if (entity is null)
            {
                await context.RespondAsync(new ForceQueueOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Discovered media '{msg.DiscoveredMediaId}' was not found."
                });
                return;
            }

            var jobId = Guid.NewGuid();
            var download = new DownloadRequested
            {
                JobId = jobId,
                CorrelationId = jobId,
                CausationId = null,
                MessageId = Guid.NewGuid(),
                OperationKey = $"force-queue/discovered-media/{entity.Id}/{jobId:N}",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                SourceUrl = entity.CanonicalUrl,
                RequestedBy = msg.RequestedBy,
                StorageKey = msg.StorageKey,
                ForceDownload = true,
                MediaKind = MediaKind.Video,
                YtDlpOptions = msg.YtDlpOptions,
                CookieSecretPath = msg.CookieSecretPath,
                Priority = msg.Priority,
                FetchComments = msg.FetchComments,
                EncodeAudioRendition = msg.EncodeForPlaylist,
                SourceKind = DownloadSourceKind.Channel
            };
            var group = new DownloadGroupRequested
            {
                GroupId = jobId,
                CorrelationId = jobId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"group/{jobId:N}/requested",
                OccurredAt = download.OccurredAt,
                Kind = DownloadGroupKind.Direct,
                SourceUrl = download.SourceUrl,
                RequestedBy = download.RequestedBy,
                StorageKey = download.StorageKey,
                Priority = download.Priority,
                DirectRequest = download
            };
            await publisher.PublishAsync(
                DownloadSubjects.GroupRequested,
                group,
                messageId: group.MessageId.ToString("N"));

            await context.RespondAsync(new ForceQueueOperationResponseMessage { Success = true, JobId = jobId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed force-queueing discovered media {DiscoveredMediaId}", msg.DiscoveredMediaId);
            await context.RespondAsync(new ForceQueueOperationResponseMessage
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to force-queue discovered media."
            });
        }
    }

    private Task PublishDownloadRequestedAsync(UpsertDiscoveredMediaBatchRequestMessage request, DiscoveredMediaCandidate candidate)
    {
        // A collection request gets one stable job id per entry. Including the collection's
        // correlation id keeps redelivery idempotent while allowing a later full-channel request
        // to create a fresh set of independently retryable jobs.
        var seed = $"{request.CorrelationId:N}:{request.CreatorSourceId}:{candidate.Platform}:{candidate.Extractor}:{candidate.ExternalMediaId}";
        var jobId = DeterministicGuid(seed);
        var messageId = DeterministicGuid($"{seed}:download-requested");
        var operationKey = $"creator-discovery/{request.CreatorSourceId}/{candidate.Platform}/{candidate.Extractor}/{candidate.ExternalMediaId}";
        return publisher.PublishAsync(
            DownloadSubjects.DownloadRequested,
            new DownloadRequested
            {
                JobId = jobId,
                CorrelationId = request.CorrelationId == Guid.Empty ? jobId : request.CorrelationId,
                CausationId = null,
                MessageId = messageId,
                OperationKey = operationKey,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                SourceUrl = candidate.CanonicalUrl,
                RequestedBy = request.RequestedBy ?? $"schedule:{request.ScheduleKey}",
                StorageKey = request.StorageKey,
                ForceDownload = request.ForceDownload,
                MediaKind = MediaKind.Video,
                YtDlpOptions = request.YtDlpOptions,
                CookieSecretPath = request.CookieSecretPath,
                Priority = request.Priority,
                FetchComments = request.FetchComments,
                EncodeAudioRendition = request.EncodeForPlaylist,
                SourceKind = DownloadSourceKind.Channel
            },
            messageId: messageId.ToString("N"));
    }

    private Task QueueInitialMetadataRefreshAsync(long sourceId, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var idempotencyKey = $"creator-source/{sourceId}/initial-metadata/{Guid.NewGuid():N}";
        return publisher.PublishAsync(
            BackgroundJobSubjects.ChannelAssetRefreshRequest,
            new ChannelAssetRefreshRequested
            {
                ScheduleKey = "creator-source-created",
                TaskType = "channel_asset_refresh",
                DueWindowUtc = now,
                IdempotencyKey = idempotencyKey,
                OccurredAt = now,
                TargetSourceId = sourceId,
                Force = true,
                MetadataOnly = true
            },
            messageId: idempotencyKey,
            cancellationToken: cancellationToken);
    }

    private Task<TResult> WithRepo<TResult>(Func<ICreatorDiscoveryRepository, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private static string? Validate(
        string platform,
        string sourceUrl,
        int incrementalPageSize,
        int consecutiveKnownThreshold,
        int fullRescanIntervalDays,
        int updateCheckIntervalHours,
        int metadataRefreshWindow,
        CreatorSourceProviderQueryLimits? providerQueryLimits)
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
        if (updateCheckIntervalHours <= 0)
            return "update_check_interval_hours must be greater than zero.";
        if (metadataRefreshWindow <= 0)
            return "metadata_refresh_window must be greater than zero.";
        if (providerQueryLimits?.Validate(MaxProviderQueryLimit) is { Count: > 0 } errors)
            return errors[0];
        return null;
    }

    private static CreatorMonitorOperationResponseMessage Failure(string message)
        => new() { Success = false, ErrorCode = "validation", ErrorMessage = message };

    private static CreatorMonitorOperationResponseMessage NotFound(long id)
        => new() { Success = false, ErrorCode = "not_found", ErrorMessage = $"Creator source '{id}' was not found." };

    private static CreatorMonitorOperationResponseMessage InternalFailure(string message)
        => new() { Success = false, ErrorCode = "internal", ErrorMessage = message };

    private static CreatorMonitorDto Map(CreatorSourceRecord record)
    {
        var entity = record.Source;
        var state = record.ScanState;
        return new CreatorMonitorDto
        {
            Id = entity.Id,
            Platform = entity.Platform,
            SourceType = entity.SourceType,
            SourceUrl = entity.SourceUrl,
            AccountId = entity.AccountId,
            ScanEnabled = entity.ScanEnabled,
            IncrementalPageSize = entity.IncrementalPageSize,
            ConsecutiveKnownThreshold = entity.ConsecutiveKnownThreshold,
            FullRescanIntervalDays = entity.FullRescanIntervalDays,
            UpdateCheckIntervalHours = entity.UpdateCheckIntervalHours,
            MetadataRefreshWindow = entity.MetadataRefreshWindow,
            LastSuccessfulScanAt = state?.LastSuccessfulScanAt,
            LastFullScanAt = state?.LastFullScanAt,
            LastSeenHighWatermark = state?.LastSeenHighWatermark,
            NextFullScanStartIndex = state?.NextFullScanStartIndex,
            ProviderQueryLimits = CreatorSourceProviderQueryLimits.FromJson(entity.ProviderQueryLimitsJson),
            CreatedAt = entity.CreatedAt,
            LastUpdated = entity.LastUpdated,
            AvatarUrl = state?.AvatarUrl,
            AvatarContentHash = state?.AvatarContentHash,
            BannerUrl = state?.BannerUrl,
            BannerContentHash = state?.BannerContentHash,
            AssetsLastRefreshedAt = state?.AssetsLastRefreshedAt,
            AssetsLastAttemptAt = state?.AssetsLastAttemptAt,
            AssetsAttemptCount = state?.AssetsAttemptCount ?? 0,
            AssetsLastError = state?.AssetsLastError
        };
    }

    private static Guid DeterministicGuid(string seed)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(bytes);
    }
}
