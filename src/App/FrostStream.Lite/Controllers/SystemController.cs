using Microsoft.AspNetCore.Mvc;
using Shared.Application;
using Shared.Auth;

namespace FrostStream.Lite.Controllers;

[ApiController]
public sealed class SystemController(ICurrentOwner currentOwner) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, bool> Capabilities =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["catalog"] = true,
            ["preferences"] = true,
            ["credentialedStorage"] = true,
            ["cookies"] = true,
            ["downloads"] = false,
            ["imports"] = false,
            ["mediaProcessing"] = false,
            ["search"] = false,
            ["liveChat"] = false,
            ["casting"] = false,
            ["schedules"] = false,
            ["backups"] = false,
            ["multiUser"] = false,
            ["remoteWorkers"] = false
        };

    [HttpGet("api/system/capabilities")]
    public IActionResult GetCapabilities() => Ok(new
    {
        edition = "lite",
        capabilities = Capabilities
    });

    [HttpGet("api/auth/config")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetAuthConfig() => Ok(new { mode = "none", edition = "lite" });

    [HttpGet("api/auth/me")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetCurrentOwner() => Ok(new
    {
        mode = "none",
        authenticated = false,
        profile = new
        {
            subject = currentOwner.Subject ?? AuthConstants.SingleUserSubject,
            name = "FrostStream Lite",
            username = (string?)null,
            email = (string?)null,
            groups = Array.Empty<string>(),
            initials = "FL"
        },
        expiresAt = (DateTimeOffset?)null
    });
}
