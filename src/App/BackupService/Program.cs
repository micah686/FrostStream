using BackupService;
using Conduit.NATS;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Backups;
using Shared.Messaging;

if (BackupCommandLine.ShouldHandle(args))
{
    Environment.ExitCode = await BackupCommandLine.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOptions<BackupServiceOptions>()
    .Bind(builder.Configuration.GetSection(BackupServiceOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Directory), "Backup:Directory is required.")
    .Validate(options => options.ScheduledRetentionCount > 0, "Backup:ScheduledRetentionCount must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BackupServiceOptions>>().Value);
builder.Services.AddSingleton<BackupJobStore>();
builder.Services.AddSingleton<BackupArchiveCatalog>();
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupCoordinator>());
builder.Services.AddHostedService<ScheduledBackupConsumer>();
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

builder.AddNats("nats", options => options.EnableTopologyProvisioning = true);
builder.Services.AddNatsTopologySource<BackgroundJobsTopology>();

var app = builder.Build();

app.MapPost("/internal/backups/jobs", async (
    CreateBackupJobRequest request,
    BackupCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    try
    {
        var job = await coordinator.QueueAsync(
            request.Name,
            request.Mode,
            request.Scheduled,
            request.IdempotencyKey,
            cancellationToken);
        return Results.Accepted($"/internal/backups/jobs/{job.JobId}", ToDto(job));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/internal/backups/jobs", (BackupJobStore store) => store.List().Select(ToDto));
app.MapGet("/internal/backups/jobs/{jobId:guid}", (Guid jobId, BackupJobStore store) =>
    store.Get(jobId) is { } job ? Results.Ok(ToDto(job)) : Results.NotFound());
app.MapGet("/internal/backups/archives", (BackupCoordinator coordinator) => coordinator.ListArchives());
app.MapPost("/internal/backups/verify", async (
    ArchiveRequest request,
    BackupCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.VerifyAsync(request.ArchivePath, cancellationToken)));
app.MapPost("/internal/backups/restore-plan", async (
    ArchiveRequest request,
    BackupCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await coordinator.BuildRestorePlanAsync(request.ArchivePath, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapHealthChecks("/health", new HealthCheckOptions());
app.MapDefaultEndpoints();

await app.RunAsync();

static BackupJobDto ToDto(BackupJobRecord job)
    => new(job.JobId, job.Status, job.ArchivePath, job.ErrorMessage, job.CreatedAt, job.CompletedAt);

internal sealed record ArchiveRequest(string ArchivePath);
