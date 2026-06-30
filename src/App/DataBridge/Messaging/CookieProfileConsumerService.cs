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
/// Owns persistence of non-secret cookie profile metadata in <c>auth.cookie_profiles</c>. All
/// operations are scoped by the owner subject supplied by WebAPI (derived from the validated token),
/// so one user can never read or mutate another user's cookie profiles. Cookie bodies are never
/// handled here — they live in OpenBAO.
/// </summary>
public sealed class CookieProfileConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<CookieProfileConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-cookies";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<CookieProfileUpsertRequestMessage>(messageBus, CookieProfileSubjects.Upsert, HandleUpsertAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CookieProfileListRequestMessage>(messageBus, CookieProfileSubjects.List, HandleListAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CookieProfileGetRequestMessage>(messageBus, CookieProfileSubjects.Get, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<CookieProfileDeleteRequestMessage>(messageBus, CookieProfileSubjects.Delete, HandleDeleteAsync, QueueGroup, stoppingToken);
        logger.LogInformation("Subscribed to cookie profile subjects.");
    }

    private async Task HandleUpsertAsync(IMessageContext<CookieProfileUpsertRequestMessage> context)
    {
        var msg = context.Message;
        if (Validate(msg.OwnerSubject, msg.ProfileKey) is { } error)
        {
            await context.RespondAsync(Failure("validation", error));
            return;
        }

        try
        {
            var dto = await scopeFactory.WithScopedAsync<DataBridgeDbContext, CookieProfileDto>(db => UpsertAsync(db, msg));
            await context.RespondAsync(new CookieProfileOperationResponseMessage { Success = true, Entity = dto });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed upserting cookie profile {ProfileKey}", msg.ProfileKey);
            await context.RespondAsync(Failure("internal", "Failed to upsert cookie profile."));
        }
    }

    private async Task HandleListAsync(IMessageContext<CookieProfileListRequestMessage> context)
    {
        var owner = context.Message.OwnerSubject;
        if (string.IsNullOrWhiteSpace(owner))
        {
            await context.RespondAsync(Failure("validation", "owner is required."));
            return;
        }

        try
        {
            var items = await scopeFactory.WithScopedAsync<DataBridgeDbContext, IReadOnlyList<CookieProfileDto>>(async db =>
            {
                var rows = await db.CookieProfiles
                    .AsNoTracking()
                    .Where(x => x.OwnerSubject == owner)
                    .OrderBy(x => x.ProfileKey)
                    .ToListAsync();
                return rows.Select(Map).ToArray();
            });

            await context.RespondAsync(new CookieProfileOperationResponseMessage { Success = true, Items = items });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing cookie profiles for {Owner}", owner);
            await context.RespondAsync(Failure("internal", "Failed to list cookie profiles."));
        }
    }

    private async Task HandleGetAsync(IMessageContext<CookieProfileGetRequestMessage> context)
    {
        var msg = context.Message;
        if (Validate(msg.OwnerSubject, msg.ProfileKey) is { } error)
        {
            await context.RespondAsync(Failure("validation", error));
            return;
        }

        try
        {
            var dto = await scopeFactory.WithScopedAsync<DataBridgeDbContext, CookieProfileDto?>(async db =>
            {
                var row = await db.CookieProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.OwnerSubject == msg.OwnerSubject && x.ProfileKey == msg.ProfileKey);
                return row is null ? null : Map(row);
            });

            await context.RespondAsync(dto is null
                ? Failure("not_found", $"Cookie profile '{msg.ProfileKey}' was not found.")
                : new CookieProfileOperationResponseMessage { Success = true, Entity = dto });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting cookie profile {ProfileKey}", msg.ProfileKey);
            await context.RespondAsync(Failure("internal", "Failed to get cookie profile."));
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<CookieProfileDeleteRequestMessage> context)
    {
        var msg = context.Message;
        if (Validate(msg.OwnerSubject, msg.ProfileKey) is { } error)
        {
            await context.RespondAsync(Failure("validation", error));
            return;
        }

        try
        {
            var deleted = await scopeFactory.WithScopedAsync<DataBridgeDbContext, bool>(async db =>
            {
                var row = await db.CookieProfiles
                    .FirstOrDefaultAsync(x => x.OwnerSubject == msg.OwnerSubject && x.ProfileKey == msg.ProfileKey);
                if (row is null)
                {
                    return false;
                }

                db.CookieProfiles.Remove(row);
                await db.SaveChangesAsync();
                return true;
            });

            await context.RespondAsync(deleted
                ? new CookieProfileOperationResponseMessage { Success = true }
                : Failure("not_found", $"Cookie profile '{msg.ProfileKey}' was not found."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting cookie profile {ProfileKey}", msg.ProfileKey);
            await context.RespondAsync(Failure("internal", "Failed to delete cookie profile."));
        }
    }

    private async Task<CookieProfileDto> UpsertAsync(DataBridgeDbContext db, CookieProfileUpsertRequestMessage msg)
    {
        var now = clock.GetCurrentInstant();
        var existing = await db.CookieProfiles
            .FirstOrDefaultAsync(x => x.OwnerSubject == msg.OwnerSubject && x.ProfileKey == msg.ProfileKey);

        if (existing is null)
        {
            existing = new CookieProfileEntity
            {
                Id = Guid.NewGuid(),
                OwnerSubject = msg.OwnerSubject,
                ProfileKey = msg.ProfileKey,
                Site = msg.Site,
                DisplayName = msg.DisplayName,
                CreatedAt = now
            };
            db.CookieProfiles.Add(existing);
        }
        else
        {
            existing.Site = msg.Site;
            existing.DisplayName = msg.DisplayName;
            existing.LastUpdated = now;
        }

        await db.SaveChangesAsync();
        return Map(existing);
    }

    private static string? Validate(string ownerSubject, string profileKey)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject))
        {
            return "owner is required.";
        }

        return string.IsNullOrWhiteSpace(profileKey) ? "profile key is required." : null;
    }

    private static CookieProfileOperationResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static CookieProfileDto Map(CookieProfileEntity entity) => new()
    {
        Id = entity.Id,
        OwnerSubject = entity.OwnerSubject,
        ProfileKey = entity.ProfileKey,
        Site = entity.Site,
        DisplayName = entity.DisplayName,
        CreatedAt = entity.CreatedAt,
        LastUpdated = entity.LastUpdated
    };
}
