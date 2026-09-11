using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Auth;
using Shared.Database;

namespace DataBridge.Messaging;

/// <summary>Idempotent fixed-owner initialization. It is never run by ordinary runtime startup.</summary>
public sealed class SingleUserOwnerInitializer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IClock clock,
    ILogger<SingleUserOwnerInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!AuthMode.IsSingleUserMode(configuration))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var now = clock.GetCurrentInstant();
        var existing = await db.FrostStreamUsers
            .FirstOrDefaultAsync(x => x.Id == AuthConstants.SingleUserId, cancellationToken);

        if (existing is null)
        {
            db.FrostStreamUsers.Add(new FrostStreamUserEntity
            {
                Id = AuthConstants.SingleUserId,
                AuthentikSubjectId = AuthConstants.SingleUserSubject,
                DisplayName = "Single User Owner",
                LastSeenAt = now
            });
        }
        else
        {
            existing.AuthentikSubjectId = AuthConstants.SingleUserSubject;
            existing.DisplayName = "Single User Owner";
            existing.LastSeenAt = now;
            existing.LastUpdated = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Single-user auth mode is active; ensured synthetic owner user {UserId}.", AuthConstants.SingleUserId);
    }
}
