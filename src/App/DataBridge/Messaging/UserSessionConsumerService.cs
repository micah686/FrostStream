using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Owns local persistence of FrostStream users. On each validated session (BFF login/refresh)
/// WebAPI publishes a <see cref="UserSessionUpsertRequestMessage"/>; this consumer upserts the
/// <c>auth.froststream_users</c> row keyed by the Authentik subject so WebAPI never has to trust
/// free-text identity fields.
/// </summary>
public sealed class UserSessionConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<UserSessionConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-users";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<UserSessionUpsertRequestMessage>(messageBus, UserSessionSubjects.Upsert, HandleUpsertAsync, QueueGroup, stoppingToken);
        logger.LogInformation("Subscribed to user session subjects.");
    }

    private async Task HandleUpsertAsync(IMessageContext<UserSessionUpsertRequestMessage> context)
    {
        var msg = context.Message;
        if (string.IsNullOrWhiteSpace(msg.Subject))
        {
            await context.RespondAsync(new UserSessionUpsertResponseMessage
            {
                Success = false,
                ErrorCode = "validation",
                ErrorMessage = "subject is required."
            });
            return;
        }

        try
        {
            var userId = await scopeFactory.WithScopedAsync<DataBridgeDbContext, Guid>(db => UpsertAsync(db, msg));
            await context.RespondAsync(new UserSessionUpsertResponseMessage { Success = true, UserId = userId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed upserting FrostStream user for subject {Subject}", msg.Subject);
            await context.RespondAsync(new UserSessionUpsertResponseMessage
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to upsert user."
            });
        }
    }

    private async Task<Guid> UpsertAsync(DataBridgeDbContext db, UserSessionUpsertRequestMessage msg)
    {
        var now = clock.GetCurrentInstant();
        var displayName = string.IsNullOrWhiteSpace(msg.DisplayName) ? msg.Subject : msg.DisplayName;

        var existing = await db.FrostStreamUsers
            .FirstOrDefaultAsync(x => x.AuthentikSubjectId == msg.Subject);

        if (existing is null)
        {
            var entity = new FrostStreamUserEntity
            {
                Id = Guid.NewGuid(),
                AuthentikSubjectId = msg.Subject,
                DisplayName = displayName,
                LastSeenAt = now
            };
            db.FrostStreamUsers.Add(entity);
            await db.SaveChangesAsync();
            logger.LogInformation("Created FrostStream user {UserId} for subject {Subject}.", entity.Id, msg.Subject);
            return entity.Id;
        }

        existing.DisplayName = displayName;
        existing.LastSeenAt = now;
        existing.LastUpdated = now;
        await db.SaveChangesAsync();
        return existing.Id;
    }
}
