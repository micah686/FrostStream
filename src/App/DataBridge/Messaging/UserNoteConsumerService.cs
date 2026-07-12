using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class UserNoteConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<UserNoteConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-user-notes";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<UserNoteUpsertRequestMessage>(messageBus, UserNoteSubjects.Upsert, HandleUpsertAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserNoteGetRequestMessage>(messageBus, UserNoteSubjects.Get, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserNoteDeleteRequestMessage>(messageBus, UserNoteSubjects.Delete, HandleDeleteAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserNoteSearchRequestMessage>(messageBus, UserNoteSubjects.Search, HandleSearchAsync, QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to user note subjects.");
    }

    private async Task HandleUpsertAsync(IMessageContext<UserNoteUpsertRequestMessage> context)
    {
        try
        {
            var result = await WithRepo(repository => repository.UpsertAsync(
                context.Message.OwnerSubject,
                context.Message.TargetType,
                context.Message.TargetId,
                context.Message.Note));

            await context.RespondAsync(result.Success && result.Note is not null
                ? Success(result.Note)
                : Failure(result.ErrorCode ?? "unknown", result.ErrorMessage ?? "User note operation failed."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed upserting user note for {OwnerSubject} {TargetType}/{TargetId}.",
                context.Message.OwnerSubject,
                context.Message.TargetType,
                context.Message.TargetId);
            await context.RespondAsync(Failure("internal_error", "Internal user note service error."));
        }
    }

    private async Task HandleGetAsync(IMessageContext<UserNoteGetRequestMessage> context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.Message.OwnerSubject))
            {
                await context.RespondAsync(Failure("validation", "owner is required."));
                return;
            }

            var note = await WithRepo(repository => repository.GetAsync(
                context.Message.OwnerSubject,
                context.Message.TargetType,
                context.Message.TargetId));

            await context.RespondAsync(note is null
                ? Failure("not_found", "Note was not found.")
                : Success(note));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting user note for {OwnerSubject} {TargetType}/{TargetId}.",
                context.Message.OwnerSubject,
                context.Message.TargetType,
                context.Message.TargetId);
            await context.RespondAsync(Failure("internal_error", "Internal user note service error."));
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<UserNoteDeleteRequestMessage> context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.Message.OwnerSubject))
            {
                await context.RespondAsync(Failure("validation", "owner is required."));
                return;
            }

            var deleted = await WithRepo(repository => repository.DeleteAsync(
                context.Message.OwnerSubject,
                context.Message.TargetType,
                context.Message.TargetId));

            await context.RespondAsync(deleted
                ? new UserNoteResponseMessage { Success = true }
                : Failure("not_found", "Note was not found."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting user note for {OwnerSubject} {TargetType}/{TargetId}.",
                context.Message.OwnerSubject,
                context.Message.TargetType,
                context.Message.TargetId);
            await context.RespondAsync(Failure("internal_error", "Internal user note service error."));
        }
    }

    private async Task HandleSearchAsync(IMessageContext<UserNoteSearchRequestMessage> context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.Message.OwnerSubject))
            {
                await context.RespondAsync(SearchFailure("validation", "owner is required."));
                return;
            }

            var result = await WithRepo(repository => repository.SearchAsync(
                context.Message.OwnerSubject,
                context.Message.Query,
                context.Message.TargetType,
                context.Message.PageSize,
                context.Message.PageOffset));

            await context.RespondAsync(new UserNoteSearchResponseMessage
            {
                Success = true,
                Items = result.Items.Select(Map).ToArray(),
                TotalCount = result.TotalCount,
                HasMore = result.HasMore
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed searching user notes for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(SearchFailure("internal_error", "Internal user note service error."));
        }
    }

    private Task<TResult> WithRepo<TResult>(Func<IUserNotesRepository, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private static UserNoteResponseMessage Success(UserNoteEntity entity)
        => new() { Success = true, Note = Map(entity) };

    private static UserNoteResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static UserNoteSearchResponseMessage SearchFailure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static UserNoteDto Map(UserNoteEntity entity)
        => new()
        {
            TargetType = entity.TargetType,
            TargetId = entity.TargetId,
            Note = entity.Note,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
}
