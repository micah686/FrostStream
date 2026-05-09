using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Metadata;

public sealed class MetadataQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataQueryConsumerService> logger) : BackgroundService
{
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataGetRequestMessage>(
            MetadataSubjects.Get,
            HandleGetAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataTechnicalRequestMessage>(
            MetadataSubjects.GetTechnical,
            HandleTechnicalAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataAccountsListRequestMessage>(
            MetadataSubjects.AccountsList,
            HandleAccountsListAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataAccountGetRequestMessage>(
            MetadataSubjects.AccountsGet,
            HandleAccountGetAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataTaxonomyListRequestMessage>(
            MetadataSubjects.TaxonomyTagsList,
            context => HandleTaxonomyListAsync(context, MetadataTaxonomyKind.Tags),
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataTaxonomyListRequestMessage>(
            MetadataSubjects.TaxonomyCategoriesList,
            context => HandleTaxonomyListAsync(context, MetadataTaxonomyKind.Categories),
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<MetadataTaxonomyListRequestMessage>(
            MetadataSubjects.TaxonomyGenresList,
            context => HandleTaxonomyListAsync(context, MetadataTaxonomyKind.Genres),
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken));

        logger.LogInformation("Subscribed to metadata Postgres query subjects.");

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
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task HandleGetAsync(IMessageContext<MetadataGetRequestMessage> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IMetadataReadService>();
            var item = await query.GetDetailAsync(context.Message.MediaGuid);
            await context.RespondAsync(item is null
                ? new MetadataGetResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Media '{context.Message.MediaGuid}' was not found."
                }
                : new MetadataGetResponseMessage { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling metadata detail query for {MediaGuid}", context.Message.MediaGuid);
            await context.RespondAsync(new MetadataGetResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata service error."
            });
        }
    }

    private async Task HandleTechnicalAsync(IMessageContext<MetadataTechnicalRequestMessage> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IMetadataReadService>();
            var item = await query.GetTechnicalAsync(context.Message.MediaGuid);
            await context.RespondAsync(item is null
                ? new MetadataTechnicalResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Technical metadata for '{context.Message.MediaGuid}' was not found."
                }
                : new MetadataTechnicalResponseMessage { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling technical metadata query for {MediaGuid}", context.Message.MediaGuid);
            await context.RespondAsync(new MetadataTechnicalResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata service error."
            });
        }
    }

    private async Task HandleAccountsListAsync(IMessageContext<MetadataAccountsListRequestMessage> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IMetadataReadService>();
            var result = await query.ListAccountsAsync(
                context.Message.PageSize,
                context.Message.After,
                context.Message.Platform);

            await context.RespondAsync(new MetadataAccountsListResponseMessage
            {
                Success = true,
                Items = result.Items,
                NextCursor = result.NextCursor,
                HasMore = result.HasMore
            });
        }
        catch (InvalidMetadataCursorException ex)
        {
            await context.RespondAsync(new MetadataAccountsListResponseMessage
            {
                Success = false,
                ErrorCode = "invalid_cursor",
                ErrorMessage = ex.Message
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling account list query.");
            await context.RespondAsync(new MetadataAccountsListResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata service error."
            });
        }
    }

    private async Task HandleAccountGetAsync(IMessageContext<MetadataAccountGetRequestMessage> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IMetadataReadService>();
            var item = await query.GetAccountAsync(context.Message.AccountId);
            await context.RespondAsync(item is null
                ? new MetadataAccountGetResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Account '{context.Message.AccountId}' was not found."
                }
                : new MetadataAccountGetResponseMessage { Success = true, Item = item });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling account get query for {AccountId}", context.Message.AccountId);
            await context.RespondAsync(new MetadataAccountGetResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata service error."
            });
        }
    }

    private async Task HandleTaxonomyListAsync(
        IMessageContext<MetadataTaxonomyListRequestMessage> context,
        MetadataTaxonomyKind kind)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IMetadataReadService>();
            var result = await query.ListTaxonomyAsync(
                kind,
                context.Message.PageSize,
                context.Message.PageOffset,
                context.Message.Search);

            await context.RespondAsync(new MetadataTaxonomyListResponseMessage
            {
                Success = true,
                Items = result.Items,
                Total = result.Total
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling metadata taxonomy query for {Kind}", kind);
            await context.RespondAsync(new MetadataTaxonomyListResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal metadata service error."
            });
        }
    }
}
