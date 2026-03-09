namespace WebAPI.Middleware;

/// <summary>
/// Middleware that extracts or generates a correlation ID for each request
/// and adds it to the logger scope and response headers for distributed tracing.
/// </summary>
public static class CorrelationMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>
    /// Adds correlation ID handling to the request pipeline.
    /// The correlation ID is extracted from the request header or generated if not present.
    /// It is added to the logger scope and included in the response headers.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N");

            // Store in HttpContext.Items for access throughout the request
            context.Items["CorrelationId"] = correlationId;

            // Add to response headers
            context.Response.Headers.Append(CorrelationIdHeader, correlationId);

            // Create a logger scope with the correlation ID
            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("CorrelationMiddleware");

            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["RequestPath"] = context.Request.Path,
                ["RequestMethod"] = context.Request.Method
            }))
            {
                await next();
            }
        });
    }

    /// <summary>
    /// Gets the correlation ID for the current request from HttpContext.Items.
    /// </summary>
    public static string? GetCorrelationId(this HttpContext context)
    {
        return context.Items.TryGetValue("CorrelationId", out var value)
            ? value?.ToString()
            : null;
    }
}
