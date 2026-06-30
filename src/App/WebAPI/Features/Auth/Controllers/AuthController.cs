using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Auth.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IConfiguration configuration,
    IMessageBus messageBus,
    IOpenFgaTupleWriter tupleWriter,
    ILogger<AuthController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpGet("config")]
    [AllowAnonymous]
    [EndpointSummary("Get authentication configuration")]
    [EndpointDescription("Returns the active FrostStream authentication mode and non-secret client configuration needed by the frontend. The endpoint is intentionally anonymous so the UI can decide whether to use single-user mode or start the external OIDC login flow before calling protected API routes.")]
    public ActionResult<AuthConfigResponse> GetConfig()
    {
        var singleUserMode = AuthMode.IsSingleUserMode(configuration);
        return Ok(new AuthConfigResponse
        {
            Mode = singleUserMode ? "single-user" : "multi-user",
            Authority = singleUserMode ? null : configuration["Auth:Authority"],
            Audience = configuration["Auth:Audience"] ?? "froststream-api"
        });
    }

    /// <summary>
    /// Called by the SvelteKit BFF after a successful login/refresh. Upserts the local FrostStream
    /// user from the validated token and reconciles the caller's Authentik groups into OpenFGA
    /// membership tuples so authorization reflects the current group set.
    /// </summary>
    [HttpPost("session")]
    [Authorize(Policy = AuthPolicies.Authenticated)]
    [EndpointSummary("Synchronize the authenticated session")]
    [EndpointDescription("Upserts the local user record keyed by the Authentik subject and refreshes the user's OpenFGA group membership tuples. Intended to be called server-side by the frontend BFF immediately after the OIDC code exchange and on token refresh.")]
    public async Task<ActionResult<AuthSessionResponse>> SyncSession(CancellationToken cancellationToken)
    {
        var subject = AuthConstants.FindSubject(User);
        if (subject is null)
        {
            return Unauthorized();
        }

        var groups = User.FindAll(AuthConstants.GroupsClaim)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var displayName = FirstNonBlank(
            User.FindFirst(AuthConstants.PreferredUsernameClaim)?.Value,
            User.FindFirst("name")?.Value,
            User.FindFirst(ClaimTypes.Name)?.Value,
            subject) ?? subject;

        var email = FirstNonBlank(
            User.FindFirst("email")?.Value,
            User.FindFirst(ClaimTypes.Email)?.Value);

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
                return StatusCode(StatusCodes.Status502BadGateway, "Failed to persist the user session.");
            }

            userId = response.UserId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting user upsert for subject {Subject}", subject);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "User service is unavailable.");
        }

        // Group tuple sync is best-effort: a failure here should not break login. It is logged and
        // retried on the next session sync.
        try
        {
            await tupleWriter.SyncUserGroupsAsync(subject, groups, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed syncing OpenFGA group tuples for subject {Subject}", subject);
        }

        return Ok(new AuthSessionResponse
        {
            UserId = userId,
            Subject = subject,
            DisplayName = displayName,
            Groups = groups
        });
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed record AuthConfigResponse
{
    public required string Mode { get; init; }

    public string? Authority { get; init; }

    public required string Audience { get; init; }
}

public sealed record AuthSessionResponse
{
    public required Guid UserId { get; init; }

    public required string Subject { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyList<string> Groups { get; init; }
}
