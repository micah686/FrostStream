using System.Text.Json;
using System.Globalization;
using Conduit.NATS;
using DataBridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class ImportSessionRequestReplyService(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    Func<string, IObjectStore> objectStoreFactory,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ImportSessionRequestReplyService> logger) : SubscriptionBackgroundService
{
    private static readonly TimeSpan ScanIngestTimeout = TimeSpan.FromMinutes(15);
    private const int ProbeBatchSize = 25;
    private const int EnrichBatchLimit = 1000;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<ImportSessionCreateRequest>(messageBus, ImportSessionSubjects.Create, HandleCreateAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionListRequest>(messageBus, ImportSessionSubjects.List, HandleListAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionGetRequest>(messageBus, ImportSessionSubjects.Get, HandleGetAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionItemsListRequest>(messageBus, ImportSessionSubjects.ItemsList, HandleItemsListAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionItemPatchRequest>(messageBus, ImportSessionSubjects.ItemsPatch, HandleItemPatchAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionItemsBulkRequest>(messageBus, ImportSessionSubjects.ItemsBulk, HandleItemsBulkAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionMappingApplyRequest>(messageBus, ImportSessionSubjects.MappingApply, HandleMappingApplyAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionMappingTemplateRequest>(messageBus, ImportSessionSubjects.MappingTemplate, HandleMappingTemplateAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionMetadataRefreshRequest>(messageBus, ImportSessionSubjects.MetadataRefresh, HandleMetadataRefreshAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionEnrichRequest>(messageBus, ImportSessionSubjects.Enrich, HandleEnrichAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionCommitRequest>(messageBus, ImportSessionSubjects.Commit, HandleCommitAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionRetryFailedRequest>(messageBus, ImportSessionSubjects.RetryFailed, HandleRetryFailedAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionCancelRequest>(messageBus, ImportSessionSubjects.Cancel, HandleCancelAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionScanIngestRequest>(messageBus, ImportSessionSubjects.ScanIngest, HandleScanIngestAsync, ImportSessionSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<ImportSessionScanFailedRequest>(messageBus, ImportSessionSubjects.ScanFailed, HandleScanFailedAsync, ImportSessionSubjects.QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to import-session request/reply subjects.");
    }

    private async Task HandleCreateAsync(IMessageContext<ImportSessionCreateRequest> context)
    {
        try
        {
            var request = context.Message;
            if (request.SourceKind != ImportSessionSourceKind.WorkerIncoming)
            {
                await context.RespondAsync(FailCreate("validation", "Only worker incoming scans are supported in this build."));
                return;
            }

            if (string.IsNullOrWhiteSpace(request.StorageKey))
            {
                await context.RespondAsync(FailCreate("validation", "storageKey is required."));
                return;
            }

            var sessionId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            ImportSessionDto session;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                session = await repo.CreateAsync(request, sessionId, correlationId);
            }

            var messageId = Guid.NewGuid();
            var command = new ScanLocalImportSourceCommand
            {
                JobId = sessionId,
                CorrelationId = correlationId,
                CausationId = null,
                MessageId = messageId,
                OperationKey = $"import-session/{sessionId:N}/scan",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                SessionId = sessionId,
                SourceKind = request.SourceKind,
                SubPath = request.SubPath,
                StorageKey = request.StorageKey.Trim(),
                RequiredWorkerTag = string.IsNullOrWhiteSpace(request.WorkerTag) ? null : request.WorkerTag.Trim()
            };

            var subject = string.IsNullOrWhiteSpace(command.RequiredWorkerTag)
                ? LocalImportSubjects.ScanLocalImportSourceCommand
                : LocalImportSubjects.ScanLocalImportSourceCommandForTag(command.RequiredWorkerTag);
            await publisher.PublishAsync(subject, command, messageId: messageId.ToString("N"));
            await PublishStateChangedAsync(session);

            await context.RespondAsync(new ImportSessionCreateResponse { Success = true, Session = session });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed creating import session.");
            await context.RespondAsync(FailCreate("internal_error", "Internal import-session service error."));
        }
    }

    private async Task HandleListAsync(IMessageContext<ImportSessionListRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var rows = await repo.ListAsync(context.Message);
            var limit = Math.Clamp(context.Message.Limit, 1, 100);
            var items = rows.Count > limit ? rows.Take(limit).ToList() : rows;
            await context.RespondAsync(new ImportSessionListResponse
            {
                Success = true,
                Items = items,
                NextSessionId = rows.Count > limit ? rows[limit - 1].SessionId : null
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing import sessions.");
            await context.RespondAsync(new ImportSessionListResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal import-session service error."
            });
        }
    }

    private async Task HandleGetAsync(IMessageContext<ImportSessionGetRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var session = await repo.GetAsync(context.Message.SessionId);
            await context.RespondAsync(session is null
                ? new ImportSessionGetResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found." }
                : new ImportSessionGetResponse { Success = true, Session = session });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting import session {SessionId}.", context.Message.SessionId);
            await context.RespondAsync(new ImportSessionGetResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal import-session service error."
            });
        }
    }

    private async Task HandleItemsListAsync(IMessageContext<ImportSessionItemsListRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var result = await repo.ListItemsAsync(context.Message);
            await context.RespondAsync(new ImportSessionItemsListResponse
            {
                Success = true,
                Items = result.Items,
                NextItemId = result.NextItemId,
                TotalCount = result.TotalCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing import session items for {SessionId}.", context.Message.SessionId);
            await context.RespondAsync(new ImportSessionItemsListResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal import-session service error."
            });
        }
    }

    private async Task HandleScanIngestAsync(IMessageContext<ImportSessionScanIngestRequest> context)
    {
        var request = context.Message;
        var tempFile = Path.Combine(Path.GetTempPath(), $"froststream-import-session-{request.SessionId:N}-{Guid.NewGuid():N}.ndjson");

        try
        {
            using var timeoutCts = new CancellationTokenSource(ScanIngestTimeout);
            var objectStore = objectStoreFactory(request.ObjectBucket);
            await using (var file = File.Create(tempFile))
            {
                await objectStore.GetAsync(request.ObjectKey, file, timeoutCts.Token);
            }

            var items = await ReadItemsAsync(tempFile, timeoutCts.Token);
            ImportSessionDto? session;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                session = await repo.IngestScannedItemsAsync(request.SessionId, items, timeoutCts.Token);
            }

            await objectStore.DeleteAsync(request.ObjectKey, CancellationToken.None);

            if (session is null)
            {
                await context.RespondAsync(new ImportSessionScanIngestResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = "Import session was not found."
                });
                return;
            }

            await PublishStateChangedAsync(session);
            await ScheduleProbeBatchAsync(session);
            await context.RespondAsync(new ImportSessionScanIngestResponse { Success = true, Session = session });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed ingesting scan object {Bucket}/{Key} for import session {SessionId}.", request.ObjectBucket, request.ObjectKey, request.SessionId);
            ImportSessionDto? failed = null;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                failed = await repo.MarkScanFailedAsync(request.SessionId, "Failed to ingest scanned item listing.");
            }
            catch (Exception markEx)
            {
                logger.LogWarning(markEx, "Failed marking import session {SessionId} scan failed.", request.SessionId);
            }

            if (failed is not null)
                await PublishStateChangedAsync(failed);

            await context.RespondAsync(new ImportSessionScanIngestResponse
            {
                Success = false,
                ErrorCode = "scan_ingest_failed",
                ErrorMessage = "Failed to ingest scanned item listing."
            });
        }
        finally
        {
            try { File.Delete(tempFile); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed deleting import-session temp file {TempFile}.", tempFile); }
        }
    }

    private async Task HandleScanFailedAsync(IMessageContext<ImportSessionScanFailedRequest> context)
    {
        try
        {
            ImportSessionDto? session;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                session = await repo.MarkScanFailedAsync(context.Message.SessionId, context.Message.ErrorMessage);
            }

            if (session is null)
            {
                await context.RespondAsync(new ImportSessionScanFailedResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = "Import session was not found."
                });
                return;
            }

            await PublishStateChangedAsync(session);
            await context.RespondAsync(new ImportSessionScanFailedResponse { Success = true, Session = session });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed marking import session {SessionId} scan failed.", context.Message.SessionId);
            await context.RespondAsync(new ImportSessionScanFailedResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal import-session service error."
            });
        }
    }

    public async Task HandleItemsProbedAsync(ImportSessionItemsProbed message)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        var session = await repo.ApplyProbeResultsAsync(message.SessionId, message.Results);
        if (message.Failures.Count > 0)
            session = await repo.ApplyProbeFailuresAsync(message.SessionId, message.Failures);
        if (session is not null)
        {
            await PublishStateChangedAsync(session);
            await ScheduleProbeBatchAsync(session);
        }
    }

    public async Task HandleItemsProbeFailedAsync(ImportSessionItemsProbeFailed message)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        var session = await repo.ApplyProbeFailuresAsync(message.SessionId, message.Failures);
        if (session is not null)
        {
            await PublishStateChangedAsync(session);
            await ScheduleProbeBatchAsync(session);
        }
    }

    private async Task HandleItemPatchAsync(IMessageContext<ImportSessionItemPatchRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var result = await repo.PatchItemAsync(context.Message);
            await context.RespondAsync(result.Item is null
                ? new ImportSessionItemPatchResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session item was not found." }
                : new ImportSessionItemPatchResponse { Success = true, Item = result.Item, Session = result.Session });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed patching import session item {ItemId}.", context.Message.ItemId);
            await context.RespondAsync(new ImportSessionItemPatchResponse { Success = false, ErrorCode = "internal_error", ErrorMessage = "Internal import-session service error." });
        }
    }

    private async Task HandleItemsBulkAsync(IMessageContext<ImportSessionItemsBulkRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var result = await repo.ApplyBulkAsync(context.Message);
            await context.RespondAsync(result.Session is null
                ? new ImportSessionItemsBulkResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found." }
                : new ImportSessionItemsBulkResponse { Success = true, AffectedCount = result.AffectedCount, Session = result.Session });
            if (context.Message.Action == ImportSessionBulkAction.Include && result.Session is not null)
                await ScheduleProbeBatchAsync(result.Session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed applying import session bulk action {Action}.", context.Message.Action);
            await context.RespondAsync(new ImportSessionItemsBulkResponse { Success = false, ErrorCode = "internal_error", ErrorMessage = "Internal import-session service error." });
        }
    }

    private async Task HandleMappingApplyAsync(IMessageContext<ImportSessionMappingApplyRequest> context)
    {
        var request = context.Message;
        var tempFile = Path.Combine(Path.GetTempPath(), $"froststream-import-mapping-{request.SessionId:N}-{Guid.NewGuid():N}");
        try
        {
            var objectStore = objectStoreFactory(request.ObjectBucket);
            await using (var file = File.Create(tempFile))
            {
                await objectStore.GetAsync(request.ObjectKey, file);
            }

            var rows = await ReadMappingRowsAsync(tempFile, request.Format);
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var result = await repo.ApplyMappingAsync(request.SessionId, rows, request.ObjectBucket, request.ObjectKey, request.Format);
            await context.RespondAsync(result.Session is null
                ? new ImportSessionMappingApplyResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found.", UnmatchedCount = result.UnmatchedCount }
                : new ImportSessionMappingApplyResponse
                {
                    Success = true,
                    MatchedCount = result.MatchedCount,
                    UnmatchedCount = result.UnmatchedCount,
                    Session = result.Session
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed applying import session mapping for {SessionId}.", request.SessionId);
            await context.RespondAsync(new ImportSessionMappingApplyResponse { Success = false, ErrorCode = "mapping_failed", ErrorMessage = "Failed to apply mapping file." });
        }
        finally
        {
            try { File.Delete(tempFile); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed deleting import mapping temp file {TempFile}.", tempFile); }
        }
    }

    private async Task HandleMappingTemplateAsync(IMessageContext<ImportSessionMappingTemplateRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var session = await repo.GetAsync(context.Message.SessionId);
            if (session is null)
            {
                await context.RespondAsync(new ImportSessionMappingTemplateResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = "Import session was not found."
                });
                return;
            }

            var items = await repo.ListMappingTemplateAsync(context.Message.SessionId);
            await context.RespondAsync(new ImportSessionMappingTemplateResponse { Success = true, Items = items });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed generating import mapping template for {SessionId}.", context.Message.SessionId);
            await context.RespondAsync(new ImportSessionMappingTemplateResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Failed to generate the mapping template."
            });
        }
    }

    private async Task HandleEnrichAsync(IMessageContext<ImportSessionEnrichRequest> context)
    {
        var request = context.Message;
        try
        {
            if (request.Options.SleepBetweenRequestsSeconds < 3)
            {
                await context.RespondAsync(new ImportSessionEnrichResponse
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = "Sleep between requests must be at least 3 seconds."
                });
                return;
            }

            ImportSessionDto? session;
            IReadOnlyList<ImportSessionEnrichItemRef> items;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                session = await repo.GetAsync(request.SessionId);
                if (session is null)
                {
                    await context.RespondAsync(new ImportSessionEnrichResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found." });
                    return;
                }

                if (session.Status != ImportSessionStatus.Reviewing)
                {
                    await context.RespondAsync(new ImportSessionEnrichResponse
                    {
                        Success = false,
                        ErrorCode = "validation",
                        ErrorMessage = "Enrichment is only available while the session is in review.",
                        Session = session
                    });
                    return;
                }

                items = await repo.ListItemsForEnrichAsync(request.SessionId, request.ItemIds, EnrichBatchLimit);
                session = await repo.MarkEnrichmentQueuedAsync(request.SessionId, items.Select(x => x.ItemId).ToList()) ?? session;
            }

            foreach (var item in items)
            {
                var messageId = Guid.NewGuid();
                var command = new EnrichImportSessionItemCommand
                {
                    JobId = session.SessionId,
                    CorrelationId = session.CorrelationId,
                    CausationId = null,
                    MessageId = messageId,
                    OperationKey = $"import-session/{session.SessionId:N}/enrich/{item.ItemId:N}",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = item.Attempt,
                    SessionId = session.SessionId,
                    ItemId = item.ItemId,
                    SourceUrl = item.SourceUrl,
                    RelativePath = item.RelativePath,
                    Provider = item.Provider,
                    RequiredWorkerTag = session.WorkerTag,
                    Options = request.Options
                };
                var subject = string.IsNullOrWhiteSpace(session.WorkerTag)
                    ? LocalImportSubjects.EnrichImportSessionItemCommand
                    : LocalImportSubjects.EnrichImportSessionItemCommandForTag(session.WorkerTag);
                await publisher.PublishAsync(subject, command, messageId: messageId.ToString("N"));
            }

            await context.RespondAsync(new ImportSessionEnrichResponse { Success = true, QueuedCount = items.Count, Session = session });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed queueing enrichment for import session {SessionId}.", request.SessionId);
            await context.RespondAsync(new ImportSessionEnrichResponse { Success = false, ErrorCode = "internal_error", ErrorMessage = "Internal import-session service error." });
        }
    }

    private async Task HandleMetadataRefreshAsync(IMessageContext<ImportSessionMetadataRefreshRequest> context)
    {
        var request = context.Message;
        try
        {
            ImportSessionDto? session;
            IReadOnlyList<ImportSessionMetadataRefreshItemRef> items;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                session = await repo.GetAsync(request.SessionId);
                if (session is null)
                {
                    await context.RespondAsync(new ImportSessionMetadataRefreshResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found." });
                    return;
                }

                if (session.Status != ImportSessionStatus.Reviewing)
                {
                    await context.RespondAsync(new ImportSessionMetadataRefreshResponse
                    {
                        Success = false,
                        ErrorCode = "validation",
                        ErrorMessage = "Metadata refresh is only available while the session is in review.",
                        Session = session
                    });
                    return;
                }

                items = await repo.ListItemsForMetadataRefreshAsync(request.SessionId, request.ItemIds, EnrichBatchLimit);
            }

            var subject = string.IsNullOrWhiteSpace(session.WorkerTag)
                ? LocalImportSubjects.RefreshMetadataRequest
                : LocalImportSubjects.RefreshMetadataRequestForTag(session.WorkerTag);
            var refresh = await messageBus.RequestAsync<RefreshImportMetadataRequest, RefreshImportMetadataResponse>(
                subject,
                new RefreshImportMetadataRequest
                {
                    Items = items.Select(item => new RefreshImportMetadataRequestItem
                    {
                        ItemId = item.ItemId,
                        RelativePath = item.RelativePath,
                        Provider = item.Provider,
                        SourceUrl = item.SourceUrl
                    }).ToList()
                },
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            if (refresh is null)
            {
                await context.RespondAsync(new ImportSessionMetadataRefreshResponse
                {
                    Success = false,
                    ErrorCode = "worker_unavailable",
                    ErrorMessage = "No import worker answered the local metadata refresh request.",
                    Session = session
                });
                return;
            }

            if (!refresh.Success)
            {
                await context.RespondAsync(new ImportSessionMetadataRefreshResponse
                {
                    Success = false,
                    ErrorCode = refresh.ErrorCode,
                    ErrorMessage = refresh.ErrorMessage,
                    Session = session
                });
                return;
            }

            foreach (var item in refresh.Items)
            {
                var messageId = Guid.NewGuid();
                using var scope = scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                session = await repo.ApplyEnrichmentAsync(new ImportSessionItemEnriched
                {
                    JobId = session.SessionId,
                    CorrelationId = session.CorrelationId,
                    CausationId = null,
                    MessageId = messageId,
                    OperationKey = $"import-session/{session.SessionId:N}/metadata-refresh/{item.ItemId:N}",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = 1,
                    SessionId = session.SessionId,
                    ItemId = item.ItemId,
                    EnrichedMetadataJson = item.EnrichedMetadataJson,
                    Title = item.Title,
                    Provider = item.Provider,
                    SourceMediaId = item.SourceMediaId,
                    SourceUrl = item.SourceUrl,
                    InfoJsonRelativePath = item.InfoJsonRelativePath
                }) ?? session;
            }

            await context.RespondAsync(new ImportSessionMetadataRefreshResponse
            {
                Success = true,
                CheckedCount = refresh.CheckedCount,
                FoundCount = refresh.FoundCount,
                Session = session
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed refreshing local metadata sidecars for import session {SessionId}.", request.SessionId);
            await context.RespondAsync(new ImportSessionMetadataRefreshResponse { Success = false, ErrorCode = "internal_error", ErrorMessage = "Internal import-session service error." });
        }
    }

    public async Task HandleItemEnrichedAsync(ImportSessionItemEnriched message)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        var session = await repo.ApplyEnrichmentAsync(message);
        if (session is not null)
            await PublishStateChangedAsync(session);
    }

    public async Task HandleItemEnrichFailedAsync(ImportSessionItemEnrichFailed message)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        var session = await repo.ApplyEnrichFailureAsync(message);
        if (session is not null)
            await PublishStateChangedAsync(session);
    }

    private async Task HandleCommitAsync(IMessageContext<ImportSessionCommitRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var result = await repo.CommitAsync(context.Message.SessionId);
            if (result.Session is null)
            {
                await context.RespondAsync(new ImportSessionCommitResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found." });
                return;
            }

            if (result.Error is not null)
            {
                await context.RespondAsync(new ImportSessionCommitResponse { Success = false, ErrorCode = "validation", ErrorMessage = result.Error, Session = result.Session });
                return;
            }

            await PublishStateChangedAsync(result.Session);
            await context.RespondAsync(new ImportSessionCommitResponse { Success = true, Session = result.Session, ApprovedCount = result.ApprovedCount });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed committing import session {SessionId}.", context.Message.SessionId);
            await context.RespondAsync(new ImportSessionCommitResponse { Success = false, ErrorCode = "internal_error", ErrorMessage = "Internal import-session service error." });
        }
    }

    private async Task HandleRetryFailedAsync(IMessageContext<ImportSessionRetryFailedRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var result = await repo.RetryFailedAsync(context.Message.SessionId);
            await context.RespondAsync(result.Session is null
                ? new ImportSessionRetryFailedResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found." }
                : new ImportSessionRetryFailedResponse { Success = true, Session = result.Session, ResetCount = result.ResetCount });
            if (result.Session is not null)
                await PublishStateChangedAsync(result.Session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed retrying failed import session items for {SessionId}.", context.Message.SessionId);
            await context.RespondAsync(new ImportSessionRetryFailedResponse { Success = false, ErrorCode = "internal_error", ErrorMessage = "Internal import-session service error." });
        }
    }

    private async Task HandleCancelAsync(IMessageContext<ImportSessionCancelRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            var session = await repo.CancelAsync(context.Message.SessionId);
            await context.RespondAsync(session is null
                ? new ImportSessionCancelResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "Import session was not found." }
                : new ImportSessionCancelResponse { Success = true, Session = session });
            if (session is not null)
                await PublishStateChangedAsync(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed cancelling import session {SessionId}.", context.Message.SessionId);
            await context.RespondAsync(new ImportSessionCancelResponse { Success = false, ErrorCode = "internal_error", ErrorMessage = "Internal import-session service error." });
        }
    }

    private async Task ScheduleProbeBatchAsync(ImportSessionDto session)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        var items = await repo.ListItemsForProbeAsync(session.SessionId, ProbeBatchSize);
        if (items.Count == 0)
            return;

        var messageId = Guid.NewGuid();
        var command = new ProbeImportSessionItemsCommand
        {
            JobId = session.SessionId,
            CorrelationId = session.CorrelationId,
            CausationId = null,
            MessageId = messageId,
            OperationKey = $"import-session/{session.SessionId:N}/probe/{messageId:N}",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            SessionId = session.SessionId,
            RequiredWorkerTag = session.WorkerTag,
            Items = items
        };
        var subject = string.IsNullOrWhiteSpace(session.WorkerTag)
            ? LocalImportSubjects.ProbeImportSessionItemsCommand
            : LocalImportSubjects.ProbeImportSessionItemsCommandForTag(session.WorkerTag);
        await publisher.PublishAsync(subject, command, messageId: messageId.ToString("N"));
    }

    private static async Task<IReadOnlyList<ImportSessionScannedItem>> ReadItemsAsync(string path, CancellationToken ct)
    {
        var items = new List<ImportSessionScannedItem>();
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var item = JsonSerializer.Deserialize<ImportSessionScannedItem>(line, JsonOptions);
            if (item is not null)
                items.Add(item);
        }

        return items;
    }

    private static async Task<IReadOnlyList<ImportSessionMappingRow>> ReadMappingRowsAsync(string path, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<IReadOnlyList<ImportSessionMappingRow>>(stream, JsonOptions)
                   ?? [];
        }

        var rows = new List<ImportSessionMappingRow>();
        using var reader = new StreamReader(path);
        var header = await reader.ReadLineAsync();
        if (header is null)
            return rows;

        var columns = SplitCsv(header).Select(x => x.Trim()).ToList();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = SplitCsv(line);
            string? Get(string name)
            {
                var index = columns.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                return index >= 0 && index < values.Count ? values[index] : null;
            }

            var fileName = Get("fileName") ?? Get("relativePath") ?? Get("file") ?? Get("path");
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            rows.Add(new ImportSessionMappingRow
            {
                FileName = fileName.Trim(),
                Title = Get("title"),
                Provider = Get("provider"),
                SourceMediaId = Get("sourceMediaId") ?? Get("source_media_id"),
                SourceUrl = Get("sourceUrl") ?? Get("source_url")
            });
        }

        return rows;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new StringWriter(CultureInfo.InvariantCulture);
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Write('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.GetStringBuilder().Clear();
            }
            else
            {
                current.Write(ch);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private Task PublishStateChangedAsync(ImportSessionDto session)
        => messageBus.PublishAsync(ImportSessionSubjects.StateChanged, new ImportSessionStateChanged
        {
            SessionId = session.SessionId,
            Status = session.Status,
            OccurredAt = clock.GetCurrentInstant()
        });

    private static ImportSessionCreateResponse FailCreate(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }
}
