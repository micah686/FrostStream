using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace WebAPI.Auth;

public sealed class BffCookieEvents(
    BffSessionRefreshService refreshService,
    NatsBffTicketStore ticketStore,
    IOptions<FrostStreamAuthOptions> authOptions) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null ||
            !context.Properties.Items.TryGetValue(BffAuthenticationDefaults.SessionKeyProperty, out var sessionKey) ||
            string.IsNullOrWhiteSpace(sessionKey))
        {
            context.RejectPrincipal();
            return;
        }

        var ticket = new AuthenticationTicket(
            context.Principal,
            context.Properties,
            BffAuthenticationDefaults.CookieScheme);
        var result = await refreshService.ValidateAsync(sessionKey, ticket, context.HttpContext.RequestAborted);
        if (!result.IsValid || result.Principal is null)
        {
            if (result.Revoke)
            {
                await ticketStore.RemoveAsync(sessionKey, context.HttpContext.RequestAborted);
                context.Response.Cookies.Delete(BffAuthenticationDefaults.CookieName, new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    Path = "/",
                    SameSite = SameSiteMode.Lax,
                    Secure = authOptions.Value.SecureCookies
                });
            }

            context.RejectPrincipal();
            return;
        }

        context.ReplacePrincipal(result.Principal);
        context.HttpContext.Items[BffAuthenticationDefaults.CookieScheme] = BffAuthenticationDefaults.CookieScheme;
        if (result.Properties is not null && !ReferenceEquals(result.Properties, context.Properties))
        {
            CopyProperties(result.Properties, context.Properties);
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static void CopyProperties(AuthenticationProperties source, AuthenticationProperties target)
    {
        target.Items.Clear();
        foreach (var item in source.Items)
        {
            target.Items[item.Key] = item.Value;
        }

        target.AllowRefresh = source.AllowRefresh;
        target.ExpiresUtc = source.ExpiresUtc;
        target.IsPersistent = source.IsPersistent;
        target.IssuedUtc = source.IssuedUtc;
        target.RedirectUri = source.RedirectUri;
        target.StoreTokens(source.GetTokens());
    }
}
