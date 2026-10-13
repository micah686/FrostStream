using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAPI.Auth;
namespace FrostStream.Lite.Infrastructure;
[ApiController, Route("auth"), AllowAnonymous]
public sealed class LocalBrowserAuthController : ControllerBase
{
    [HttpGet("login")]
    public IActionResult Login(string? returnTo = null, string? redirectTo = null) => LocalRedirect(LocalReturnPath.Normalize(returnTo ?? redirectTo));
    [HttpPost("logout")]
    public IActionResult Logout() => Ok(new { loggedOut = true });
}
