namespace WebAPI.Auth;

public static class BffAuthenticationDefaults
{
    public const string CookieScheme = "BffCookie";
    public const string OpenIdConnectScheme = "BffOidc";
    public const string CookieName = "fs_session";
    public const string AntiforgeryCookieName = "fs_csrf";
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
    public const string SessionKeyProperty = ".froststream.session-key";
    public const string HttpClientName = "FrostStream.BffOidc";
}
