using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();

var backups = app.MapGroup("/internal/backups");
backups.MapPost("/jobs", () => Skeleton());
backups.MapGet("/jobs", () => Skeleton());
backups.MapGet("/jobs/{jobId:guid}", (Guid jobId) => Skeleton());
backups.MapGet("/backups", () => Skeleton());
backups.MapPost("/verify", () => Skeleton());

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapHealthChecks("/alive", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});

await app.RunAsync();

static IResult Skeleton() => Results.Json(
    new { error = "Backup service is not implemented yet." },
    statusCode: StatusCodes.Status501NotImplemented);
