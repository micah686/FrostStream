using Microsoft.AspNetCore.Mvc;
using Shared.Secrets;
using WebAPI.Features.Cookies.Models;

namespace WebAPI.Features.Cookies.Controllers;

/// <summary>
/// CRUD for Netscape-formatted cookie files stored in OpenBAO under
/// <c>cookies/{key}</c>. Used by the download flow to fetch member-only content.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CookiesController(ISecretStore secretStore, ILogger<CookiesController> logger) : ControllerBase
{
    private const string CookieField = "content";

    /// <summary>Stores or replaces the Netscape cookie file at <c>cookies/{key}</c>.</summary>
    [HttpPut("{key}")]
    [EndpointSummary("Store a download cookie file")]
    [EndpointDescription("Creates or replaces a Netscape-formatted cookie file in the configured secret store under the supplied key. Keys must contain only lowercase letters, digits, and hyphens and be between 2 and 100 characters; cookie contents are stored as secrets for authenticated downloads.")]
    public async Task<IActionResult> Upsert(
        string key,
        [FromBody] CookieUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValidKey(key))
            return BadRequest("Cookie key must match ^[a-z0-9-]{2,100}$.");

        try
        {
            await secretStore.WriteAsync(
                SecretPaths.ForCookies(key),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CookieField] = request.Content
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed writing cookie '{Key}' to OpenBAO", key);
            return StatusCode(StatusCodes.Status502BadGateway, "Failed to write cookie to secret store.");
        }

        return Ok(new CookieResponse(key));
    }

    /// <summary>Returns metadata about a stored cookie. The body is never returned over HTTP.</summary>
    [HttpGet("{key}")]
    [EndpointSummary("Check a download cookie file")]
    [EndpointDescription("Checks whether a cookie secret exists for the supplied key and returns only its key metadata. The stored cookie body is deliberately never exposed over HTTP; invalid keys return 400 and missing secrets return 404.")]
    public async Task<ActionResult<CookieResponse>> Get(string key, CancellationToken cancellationToken)
    {
        if (!IsValidKey(key))
            return BadRequest("Cookie key must match ^[a-z0-9-]{2,100}$.");

        IReadOnlyDictionary<string, string>? secret;
        try
        {
            secret = await secretStore.ReadAsync(SecretPaths.ForCookies(key), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed reading cookie '{Key}' from OpenBAO", key);
            return StatusCode(StatusCodes.Status502BadGateway, "Failed to read cookie from secret store.");
        }

        if (secret is null || !secret.ContainsKey(CookieField))
            return NotFound();

        return Ok(new CookieResponse(key));
    }

    [HttpDelete("{key}")]
    [EndpointSummary("Delete a download cookie file")]
    [EndpointDescription("Deletes the Netscape cookie file associated with the supplied key from the secret store. The key format is validated before deletion, and a successful request returns 204 without exposing or returning the secret contents.")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        if (!IsValidKey(key))
            return BadRequest("Cookie key must match ^[a-z0-9-]{2,100}$.");

        try
        {
            await secretStore.DeleteAsync(SecretPaths.ForCookies(key), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting cookie '{Key}' from OpenBAO", key);
            return StatusCode(StatusCodes.Status502BadGateway, "Failed to delete cookie from secret store.");
        }

        return NoContent();
    }

    private static bool IsValidKey(string key)
        => !string.IsNullOrWhiteSpace(key)
           && System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z0-9-]{2,100}$");
}
