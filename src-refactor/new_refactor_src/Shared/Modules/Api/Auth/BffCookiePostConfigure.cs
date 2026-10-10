using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace WebAPI.Auth;

public sealed class BffCookiePostConfigure(NatsBffTicketStore ticketStore)
    : IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (name == BffAuthenticationDefaults.CookieScheme)
        {
            options.SessionStore = ticketStore;
        }
    }
}
