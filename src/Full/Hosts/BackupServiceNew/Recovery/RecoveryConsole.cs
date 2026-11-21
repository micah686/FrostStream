using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace BackupServiceNew.Recovery;

internal sealed class RecoveryCredential
{
    private readonly byte[] digest;
    private readonly string? generatedToken;

    public RecoveryCredential(string? token, string? alias)
    {
        var configured = string.IsNullOrWhiteSpace(token) ? alias : token;
        if (string.IsNullOrWhiteSpace(configured))
            configured = generatedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        digest = Hash(configured);
    }

    public bool Matches(string token) => CryptographicOperations.FixedTimeEquals(digest, Hash(token));

    public void LogGeneratedToken(ILogger logger)
    {
        if (generatedToken is not null)
            logger.LogWarning("\n===== FROSTSTREAM RESTORE CONSOLE TOKEN =====\n{RecoveryToken}\n===========================================", generatedToken);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

internal static class RecoveryConsole
{
    public const string LoginLimit = "recovery-login";

    public static void AddRecoveryConsole(this WebApplicationBuilder builder, string? alias)
    {
        builder.Services.AddSingleton(new RecoveryCredential(builder.Configuration["BACKUP_RESTORE_UI_TOKEN"], alias));
        builder.Services.AddSingleton<RecoveryReadiness>();
        builder.Services.AddSingleton<RecoveryPlans>();
        builder.Services.AddSingleton<RecoveryExecution>();
        builder.Services.AddMemoryCache();
        builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/").AllowAnonymousToPage("/Login"));
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.Cookie.Name = "froststream.restore";
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.HttpOnly = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
            });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginLimit, context =>
                HttpMethods.IsPost(context.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) })
                    : RateLimitPartition.GetNoLimiter("read"));
        });
    }

    public static void UseRecoveryListener(this WebApplication app, int port)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var health = path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/alive", StringComparison.OrdinalIgnoreCase);
            var restore = context.Connection.LocalPort == port;
            // Admit only the console and liveness paths on the host-published listener.
            if (!health && IsConsole(path) != restore)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (restore)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; style-src 'self'; script-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            }
            await next();
        });
    }

    private static bool IsConsole(PathString path) =>
        !path.HasValue || path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/recovery", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/recovery.css", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/recovery.js", StringComparison.OrdinalIgnoreCase);

    public static string Time(DateTimeOffset time) => time.ToString("MMM d, yyyy · HH:mm:ss 'UTC'zzz", CultureInfo.InvariantCulture);

    public static string Bytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? FormattableString.Invariant($"{bytes / (1024d * 1024 * 1024):0.##} GiB")
        : FormattableString.Invariant($"{bytes / (1024d * 1024):0.##} MiB");
}
