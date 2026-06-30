using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using Shared.Secrets;
using WebAPI.Auth;
using WebAPI.Features.Cookies.Models;

namespace WebAPI.Features.Cookies.Controllers;

/// <summary>
/// Per-user cookie profiles. Cookie bodies are stored write-only in OpenBAO under
/// <c>cookies/users/{subject}/{profileKey}</c>; non-secret metadata lives in Postgres
/// (<c>auth.cookie_profiles</c>) via DataBridge. Every operation is scoped to the authenticated
/// subject, so one user can never see, check, or delete another user's profiles — and the cookie
/// body is never returned over HTTP, even to admins.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CookiesController(ISecretStore secretStore, IMessageBus messageBus, ILogger<CookiesController> logger) : ControllerBase
{
    private const string CookieField = "content";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Stores or replaces the caller's Netscape cookie profile.</summary>
    [HttpPut("{profileKey}")]
    [Endpoint(EndpointIds.CookiesPut)]
    [EndpointSummary("Store a cookie profile")]
    [EndpointDescription("Creates or replaces a Netscape-formatted cookie profile owned by the authenticated user. The cookie body is written to the secret store under a user-scoped path and is never returned; only non-secret metadata (site, display name, timestamps) is persisted in the database and echoed back.")]
    public async Task<ActionResult<CookieProfileResponse>> Upsert(
        string profileKey,
        [FromBody] CookieUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
        {
            return Unauthorized();
        }

        if (!SecretPaths.IsValidProfileKey(profileKey))
        {
            return BadRequest("Cookie profile key must match ^[a-z0-9-]{2,100}$.");
        }

        try
        {
            await secretStore.WriteAsync(
                SecretPaths.ForUserCookieProfile(subject, profileKey),
                new Dictionary<string, string>(StringComparer.Ordinal) { [CookieField] = request.Content },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed writing cookie profile '{ProfileKey}' to OpenBAO", profileKey);
            return StatusCode(StatusCodes.Status502BadGateway, "Failed to write cookie to secret store.");
        }

        var response = await SendAsync(
            CookieProfileSubjects.Upsert,
            new CookieProfileUpsertRequestMessage
            {
                OwnerSubject = subject,
                ProfileKey = profileKey,
                Site = request.Site,
                DisplayName = request.DisplayName
            },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to persist cookie profile metadata.");
        }

        if (!response.Success || response.Entity is null)
        {
            return MapError(response);
        }

        return Ok(Map(response.Entity));
    }

    /// <summary>Lists the caller's cookie profiles (metadata only).</summary>
    [HttpGet]
    [Endpoint(EndpointIds.CookiesList)]
    [EndpointSummary("List cookie profiles")]
    [EndpointDescription("Returns the authenticated user's cookie profiles with non-secret metadata only. Cookie bodies are never included. The list is scoped to the caller, so it cannot reveal other users' profiles.")]
    public async Task<ActionResult<IReadOnlyCollection<CookieProfileResponse>>> List(CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
        {
            return Unauthorized();
        }

        var response = await SendAsync(
            CookieProfileSubjects.List,
            new CookieProfileListRequestMessage { OwnerSubject = subject },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to list cookie profiles.");
        }

        if (!response.Success)
        {
            return MapError(response);
        }

        return Ok((response.Items ?? Array.Empty<CookieProfileDto>()).Select(Map).ToArray());
    }

    /// <summary>Returns metadata about one of the caller's cookie profiles. The body is never returned.</summary>
    [HttpGet("{profileKey}")]
    [Endpoint(EndpointIds.CookiesGet)]
    [EndpointSummary("Check a cookie profile")]
    [EndpointDescription("Returns metadata for one of the authenticated user's cookie profiles. The stored cookie body is deliberately never exposed; unknown profiles return 404 and the lookup is scoped to the caller.")]
    public async Task<ActionResult<CookieProfileResponse>> Get(string profileKey, CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
        {
            return Unauthorized();
        }

        if (!SecretPaths.IsValidProfileKey(profileKey))
        {
            return BadRequest("Cookie profile key must match ^[a-z0-9-]{2,100}$.");
        }

        var response = await SendAsync(
            CookieProfileSubjects.Get,
            new CookieProfileGetRequestMessage { OwnerSubject = subject, ProfileKey = profileKey },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to read cookie profile.");
        }

        if (!response.Success || response.Entity is null)
        {
            return MapError(response);
        }

        return Ok(Map(response.Entity));
    }

    [HttpDelete("{profileKey}")]
    [Endpoint(EndpointIds.CookiesDelete)]
    [EndpointSummary("Delete a cookie profile")]
    [EndpointDescription("Deletes one of the authenticated user's cookie profiles, removing both the secret body from the secret store and the non-secret metadata. Returns 204 on success and 404 when the profile does not exist for the caller.")]
    public async Task<IActionResult> Delete(string profileKey, CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
        {
            return Unauthorized();
        }

        if (!SecretPaths.IsValidProfileKey(profileKey))
        {
            return BadRequest("Cookie profile key must match ^[a-z0-9-]{2,100}$.");
        }

        try
        {
            await secretStore.DeleteAsync(SecretPaths.ForUserCookieProfile(subject, profileKey), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting cookie profile '{ProfileKey}' from OpenBAO", profileKey);
            return StatusCode(StatusCodes.Status502BadGateway, "Failed to delete cookie from secret store.");
        }

        var response = await SendAsync(
            CookieProfileSubjects.Delete,
            new CookieProfileDeleteRequestMessage { OwnerSubject = subject, ProfileKey = profileKey },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to delete cookie profile metadata.");
        }

        // A missing metadata row is fine on delete — the secret is already gone.
        if (!response.Success && response.ErrorCode != "not_found")
        {
            return MapError(response);
        }

        return NoContent();
    }

    private string? ResolveSubject()
    {
        var subject = AuthConstants.FindSubject(User);
        return SecretPaths.IsValidUserScope(subject) ? subject : null;
    }

    private async Task<CookieProfileOperationResponseMessage?> SendAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, CookieProfileOperationResponseMessage>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing cookie profile request on subject '{Subject}'", subject);
            return null;
        }
    }

    private ActionResult MapError(CookieProfileOperationResponseMessage response)
        => response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Cookie profile request failed.")
        };

    private static CookieProfileResponse Map(CookieProfileDto dto) => new()
    {
        ProfileKey = dto.ProfileKey,
        Site = dto.Site,
        DisplayName = dto.DisplayName,
        CreatedAt = dto.CreatedAt,
        LastUpdated = dto.LastUpdated
    };
}
