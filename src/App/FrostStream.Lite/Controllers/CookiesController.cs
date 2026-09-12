using System.ComponentModel.DataAnnotations;
using DataBridge.Lite;
using Microsoft.AspNetCore.Mvc;
using Shared.Application;
using Shared.Messaging;
using Shared.Secrets;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/user/cookies")]
public sealed class CookiesController(
    ISecretStore secretStore,
    CookieProfileApplication profiles,
    ICurrentOwner currentOwner,
    ILogger<CookiesController> logger) : ControllerBase
{
    private const string CookieField = "content";

    [HttpPut("{profileKey}")]
    public async Task<IActionResult> Upsert(
        string profileKey,
        [FromBody] CookieUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!SecretPaths.IsValidProfileKey(profileKey))
            return BadRequest("Cookie profile key must match ^[a-z0-9-]{2,100}$.");

        var path = SecretPaths.ForUserCookieProfile(Owner, profileKey);
        var previous = await secretStore.ReadAsync(path, cancellationToken);
        await secretStore.WriteAsync(
            path,
            new Dictionary<string, string>(StringComparer.Ordinal) { [CookieField] = request.Content },
            cancellationToken);
        try
        {
            var profile = await profiles.UpsertAsync(
                Owner, profileKey, request.Site, request.DisplayName, cancellationToken);
            return Ok(ToResponse(profile));
        }
        catch
        {
            try
            {
                if (previous is null)
                    await secretStore.DeleteAsync(path, CancellationToken.None);
                else
                    await secretStore.WriteAsync(path, previous, CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                logger.LogError(rollbackError, "Failed rolling back a cookie secret after metadata persistence failed.");
            }
            throw;
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok((await profiles.ListAsync(Owner, cancellationToken)).Select(ToResponse));

    [HttpGet("{profileKey}")]
    public async Task<IActionResult> Get(string profileKey, CancellationToken cancellationToken)
    {
        if (!SecretPaths.IsValidProfileKey(profileKey))
            return BadRequest("Cookie profile key must match ^[a-z0-9-]{2,100}$.");
        var profile = await profiles.GetAsync(Owner, profileKey, cancellationToken);
        return profile is null ? NotFound() : Ok(ToResponse(profile));
    }

    [HttpDelete("{profileKey}")]
    public async Task<IActionResult> Delete(string profileKey, CancellationToken cancellationToken)
    {
        if (!SecretPaths.IsValidProfileKey(profileKey))
            return BadRequest("Cookie profile key must match ^[a-z0-9-]{2,100}$.");
        await secretStore.DeleteAsync(SecretPaths.ForUserCookieProfile(Owner, profileKey), cancellationToken);
        await profiles.DeleteAsync(Owner, profileKey, cancellationToken);
        return NoContent();
    }

    private string Owner => currentOwner.Subject
        ?? throw new InvalidOperationException("The fixed Lite owner is unavailable.");

    private static object ToResponse(CookieProfileDto profile) => new
    {
        profile.ProfileKey,
        profile.Site,
        profile.DisplayName,
        profile.CreatedAt,
        profile.LastUpdated
    };
}

public sealed class CookieUpsertRequest
{
    [Required, MinLength(1)] public required string Content { get; init; }
    [StringLength(255)] public string? Site { get; init; }
    [StringLength(255)] public string? DisplayName { get; init; }
}
