using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Auth;

public sealed class CsrfProtectionMiddleware(RequestDelegate next, ILogger<CsrfProtectionMiddleware> logger)
{
    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    };

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (RequiresValidation(context))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException ex)
            {
                logger.LogInformation(ex, "Rejected a cookie-authenticated request with an invalid CSRF token.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                context.Response.Headers["X-CSRF-Token-Invalid"] = "true";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "CSRF validation failed",
                    Detail = "Fetch a fresh token from /api/auth/csrf and retry the request."
                });
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresValidation(HttpContext context)
    {
        if (!UnsafeMethods.Contains(context.Request.Method) ||
            !string.Equals(
                context.Items[BffAuthenticationDefaults.CookieScheme] as string,
                BffAuthenticationDefaults.CookieScheme,
                StringComparison.Ordinal))
        {
            return false;
        }

        return context.Request.Path.StartsWithSegments("/api") ||
               context.Request.Path.Equals("/auth/logout", StringComparison.OrdinalIgnoreCase);
    }
}
