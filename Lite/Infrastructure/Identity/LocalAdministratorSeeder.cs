using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Auth;
using Shared.Database;

namespace FrostStream.Lite.Infrastructure.Identity;

public sealed class LocalAdministratorSeeder(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<LocalAdministratorSeeder> logger)
{
    public async Task InitializeAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var now = clock.GetCurrentInstant();
        var existing = await db.FrostStreamUsers
            .FirstOrDefaultAsync(x => x.Id == AuthConstants.SingleUserId, stoppingToken);

        if (existing is null)
        {
            db.FrostStreamUsers.Add(new FrostStreamUserEntity
            {
                Id = AuthConstants.SingleUserId,
                AuthentikSubjectId = AuthConstants.SingleUserSubject,
                DisplayName = "Admin",
                LastSeenAt = now
            });
        }
        else
        {
            existing.AuthentikSubjectId = AuthConstants.SingleUserSubject;
            existing.DisplayName = "Admin";
            existing.LastSeenAt = now;
            existing.LastUpdated = now;
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Ensured local administrator {UserId}.", AuthConstants.SingleUserId);
    }
}
