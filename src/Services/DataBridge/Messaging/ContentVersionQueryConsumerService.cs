using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class ContentVersionQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<ContentVersionQueryConsumerService> logger) : BackgroundService
{
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<ContentVersionExistsRequest>(
            DownloadSubjects.ContentVersionExistsQuery,
            HandleContentVersionExistsAsync,
            queueGroup: DownloadTopology.DataBridgeQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to content-version lookup queries.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.StopAsync(cancellationToken);
            await _subscription.DisposeAsync();
            _subscription = null;
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleContentVersionExistsAsync(IMessageContext<ContentVersionExistsRequest> context)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var contentHash = NormalizeHash(context.Message.ContentHashXxh128);
            var storageKey = NormalizeStorageKey(context.Message.StorageKey);
            if (contentHash is null || storageKey is null)
            {
                await context.RespondAsync(new ContentVersionExistsResponse
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = "Content hash and storage key are required."
                });
                return;
            }

            var exists = await db.MediaContentIdVersions
                .AsNoTracking()
                .AnyAsync(x => x.ContentHashXxh128 == contentHash && x.StorageKey == storageKey);

            await context.RespondAsync(new ContentVersionExistsResponse
            {
                Success = true,
                Exists = exists
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed handling content-version lookup for ContentHash {ContentHashXxh128} StorageKey {StorageKey}",
                context.Message.ContentHashXxh128,
                context.Message.StorageKey);
            await context.RespondAsync(new ContentVersionExistsResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal content-version lookup error."
            });
        }
    }

    private static string? NormalizeHash(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();

    private static string? NormalizeStorageKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}
