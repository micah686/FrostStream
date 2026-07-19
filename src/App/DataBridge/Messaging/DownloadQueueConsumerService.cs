using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Answers NATS Core request/reply queries for the admin download-queue read surface:
/// filtered/paged job history, per-job detail, and per-job event timeline. Read-only —
/// it never mutates saga state.
/// </summary>
public sealed class DownloadQueueConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadQueueConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<DownloadQueueListRequest>(
            messageBus,
            DownloadQueueSubjects.List,
            HandleListAsync,
            queueGroup: DownloadQueueSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<DownloadQueueGetRequest>(
            messageBus,
            DownloadQueueSubjects.Get,
            HandleGetAsync,
            queueGroup: DownloadQueueSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<DownloadQueueHistoryRequest>(
            messageBus,
            DownloadQueueSubjects.History,
            HandleHistoryAsync,
            queueGroup: DownloadQueueSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<DownloadQueueMediaRequest>(
            messageBus,
            DownloadQueueSubjects.Media,
            HandleMediaAsync,
            queueGroup: DownloadQueueSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to download-queue query subjects.");
    }

    private async Task HandleListAsync(IMessageContext<DownloadQueueListRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            var page = await repo.QueryQueueAsync(context.Message);

            await context.RespondAsync(new DownloadQueueListResponse
            {
                Success = true,
                Items = page.Items,
                NextCursor = page.NextCursor,
                TotalCount = page.TotalCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling download queue list query.");
            await context.RespondAsync(new DownloadQueueListResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal download queue service error."
            });
        }
    }

    private async Task HandleGetAsync(IMessageContext<DownloadQueueGetRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            var job = await repo.GetQueueJobAsync(context.Message.JobId);

            await context.RespondAsync(job is null
                ? new DownloadQueueGetResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Download job '{context.Message.JobId}' was not found."
                }
                : new DownloadQueueGetResponse { Success = true, Job = job });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling download queue get query for {JobId}.", context.Message.JobId);
            await context.RespondAsync(new DownloadQueueGetResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal download queue service error."
            });
        }
    }

    private async Task HandleHistoryAsync(IMessageContext<DownloadQueueHistoryRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            var entries = await repo.GetQueueHistoryAsync(context.Message.JobId);

            await context.RespondAsync(entries is null
                ? new DownloadQueueHistoryResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Download job '{context.Message.JobId}' was not found."
                }
                : new DownloadQueueHistoryResponse { Success = true, Entries = entries });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling download queue history query for {JobId}.", context.Message.JobId);
            await context.RespondAsync(new DownloadQueueHistoryResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal download queue service error."
            });
        }
    }

    private async Task HandleMediaAsync(IMessageContext<DownloadQueueMediaRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            var mediaGuid = await repo.GetMediaGuidForJobAsync(context.Message.JobId);

            await context.RespondAsync(new DownloadQueueMediaResponse { Success = true, MediaGuid = mediaGuid });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling download queue media query for {JobId}.", context.Message.JobId);
            await context.RespondAsync(new DownloadQueueMediaResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal download queue service error."
            });
        }
    }
}
