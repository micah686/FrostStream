using System.Security.Claims;
using Conduit.NATS;
using Shared.Auth;
using Shared.Messaging;

namespace WebAPI.Auth;

public sealed record SessionSynchronizationResult(
    bool Success,
    Guid UserId,
    string Subject,
    string DisplayName,
    IReadOnlyList<string> Groups,
    string? ErrorMessage = null,
    bool ServiceUnavailable = false);

public interface ISessionSynchronizationService
{
    Task<SessionSynchronizationResult> SynchronizeAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

public sealed class SessionSynchronizationService(
    IMessageBus messageBus,
    IOpenFgaTupleWriter tupleWriter,
    ILogger<SessionSynchronizationService> logger) : ISessionSynchronizationService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<SessionSynchronizationResult> SynchronizeAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var subject = AuthConstants.FindSubject(principal);
        if (subject is null)
        {
            return new(false, Guid.Empty, "", "", [], "The validated principal has no subject.");
        }

        var groups = principal.FindAll(AuthConstants.GroupsClaim)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var displayName = FirstNonBlank(
            principal.FindFirst(AuthConstants.PreferredUsernameClaim)?.Value,
            principal.FindFirst("name")?.Value,
            principal.FindFirst(ClaimTypes.Name)?.Value,
            subject) ?? subject;
        var email = FirstNonBlank(
            principal.FindFirst("email")?.Value,
            principal.FindFirst(ClaimTypes.Email)?.Value);

        Guid userId;
        try
        {
            var response = await messageBus.RequestAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(
                UserSessionSubjects.Upsert,
                new UserSessionUpsertRequestMessage
                {
                    Subject = subject,
                    DisplayName = displayName,
                    Email = email,
                    Groups = groups
                },
                RequestTimeout,
                cancellationToken);

            if (response is null || !response.Success)
            {
                logger.LogWarning("User upsert failed for subject {Subject}: {Error}", subject, response?.ErrorMessage);
                return new(false, Guid.Empty, subject, displayName, groups,
                    response?.ErrorMessage ?? "Failed to persist the user session.");
            }

            userId = response.UserId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting user upsert for subject {Subject}", subject);
            return new(false, Guid.Empty, subject, displayName, groups,
                "User service is unavailable.", ServiceUnavailable: true);
        }

        try
        {
            await tupleWriter.SyncUserGroupsAsync(subject, groups, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed syncing OpenFGA group tuples for subject {Subject}", subject);
        }

        return new(true, userId, subject, displayName, groups);
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
