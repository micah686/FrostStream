using DataBridge;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Metadata;

public sealed class MetadataQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataQueryConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MetadataGetRequestMessage>(
            messageBus,
            MetadataSubjects.Get,
            HandleGetAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MetadataTechnicalRequestMessage>(
            messageBus,
            MetadataSubjects.GetTechnical,
            HandleTechnicalAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MetadataAccountsListRequestMessage>(
            messageBus,
            MetadataSubjects.AccountsList,
            HandleAccountsListAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MetadataAccountGetRequestMessage>(
            messageBus,
            MetadataSubjects.AccountsGet,
            HandleAccountGetAsync,
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MetadataTaxonomyListRequestMessage>(
            messageBus,
            MetadataSubjects.TaxonomyTagsList,
            context => HandleTaxonomyListAsync(context, MetadataTaxonomyKind.Tags),
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MetadataTaxonomyListRequestMessage>(
            messageBus,
            MetadataSubjects.TaxonomyCategoriesList,
            context => HandleTaxonomyListAsync(context, MetadataTaxonomyKind.Categories),
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<MetadataTaxonomyListRequestMessage>(
            messageBus,
            MetadataSubjects.TaxonomyGenresList,
            context => HandleTaxonomyListAsync(context, MetadataTaxonomyKind.Genres),
            queueGroup: MetadataSubjects.ProcessorsQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata Postgres query subjects.");
    }

    private async Task HandleGetAsync(IMessageContext<MetadataGetRequestMessage> context)
    {
        try
        {
            var item = await WithQuery(query => query.GetDetailAsync(context.Message.MediaGuid));
            if (item is not null)
                item = await AttachNotesAsync(item, context.Message.OwnerSubject);

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
            var item = await WithQuery(query => query.GetTechnicalAsync(context.Message.MediaGuid));
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
            var result = await WithQuery(query => query.ListAccountsAsync(
                context.Message.PageSize,
                context.Message.After,
                context.Message.Platform));
            var items = await AttachAccountNotesAsync(result.Items, context.Message.OwnerSubject);

            await context.RespondAsync(new MetadataAccountsListResponseMessage
            {
                Success = true,
                Items = items,
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
            var item = await WithQuery(query => query.GetAccountAsync(context.Message.AccountId));
            if (item is not null)
                item = await AttachAccountNoteAsync(item, context.Message.OwnerSubject);

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
            var result = await WithQuery(query => query.ListTaxonomyAsync(
                kind,
                context.Message.PageSize,
                context.Message.PageOffset,
                context.Message.Search));

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

    private Task<TResult> WithQuery<TResult>(Func<IMetadataReadService, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private async Task<MetadataDetailDto> AttachNotesAsync(MetadataDetailDto item, string? ownerSubject)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject))
            return item;

        var videoNotes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<Guid, string>>(
            repository => repository.GetVideoNotesAsync(ownerSubject, [item.MediaGuid]));
        var channelNotes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<long, string>>(
            repository => repository.GetChannelNotesAsync(ownerSubject, [item.Account.AccountId]));

        return item with
        {
            UserNote = videoNotes.GetValueOrDefault(item.MediaGuid),
            Account = item.Account with { UserNote = channelNotes.GetValueOrDefault(item.Account.AccountId) }
        };
    }

    private async Task<IReadOnlyList<AccountSummaryDto>> AttachAccountNotesAsync(
        IReadOnlyList<AccountSummaryDto> items,
        string? ownerSubject)
    {
        if (items.Count == 0 || string.IsNullOrWhiteSpace(ownerSubject))
            return items;

        var ids = items.Select(x => x.AccountId).ToArray();
        var notes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<long, string>>(
            repository => repository.GetChannelNotesAsync(ownerSubject, ids));

        return items
            .Select(x => x with { UserNote = notes.GetValueOrDefault(x.AccountId) })
            .ToArray();
    }

    private async Task<AccountDto> AttachAccountNoteAsync(AccountDto item, string? ownerSubject)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject))
            return item;

        var notes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<long, string>>(
            repository => repository.GetChannelNotesAsync(ownerSubject, [item.AccountId]));

        return item with { UserNote = notes.GetValueOrDefault(item.AccountId) };
    }
}
