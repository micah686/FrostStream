using System.Security.Cryptography;
using System.Text;
using BackupService;
using BackupService.PgBackRest;
using BackupServiceNew;
using BackupServiceNew.PgBackRestWrapper;
using Conduit.NATS;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using NodaTime;
using Shared.Backups;
using Shared.Messaging;
using Shared.Messaging.Adapters;
using IMessageBus = FrostStream.ApplicationContracts.IMessageBus;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var configuration = builder.Configuration.GetSection("Backup");
var settings = configuration.Get<BackupServiceOptions>() ?? new BackupServiceOptions();
var root = Path.GetFullPath(settings.Directory);
var wrapper = new WrapperOptions
{
    Stanza = configuration["Stanza"] ?? "main",
    DataPath = settings.PgDataPath,
    RepositoryPath = Path.Combine(root, "pgbackrest"),
    StatePath = Path.Combine(root, "wrapper-state"),
    ConfigPath = configuration["ConfigPath"] ?? "/etc/pgbackrest/pgbackrest.conf",
    SocketPath = settings.PostgresSocketDirectory,
    Port = settings.PostgresPort,
    DatabaseUser = settings.PostgresUser
};
var apiToken = Encoding.UTF8.GetBytes(configuration["ApiToken"] ?? "");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(wrapper);
builder.Services.AddSingleton<PgBackRestClient>();
builder.Services.AddSingleton<OpenBaoPairing>();
builder.Services.AddSingleton<BackupRepository>();
builder.Services.AddSingleton<BackupJobs>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupJobs>());
builder.Services.AddNatsApplicationTransport();
builder.Services.AddSingleton<IBackgroundRunReporter>(sp => new BackgroundRunReporter(
    sp.GetRequiredService<IMessageBus>(), SystemClock.Instance, "backupservice",
    sp.GetRequiredService<ILogger<BackgroundRunReporter>>()));
builder.AddNats("nats", options =>
{
    options.EnableTopologyProvisioning = false;
    options.ValidateConnectionOnStart = false;
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal/backups"))
    {
        var supplied = Encoding.UTF8.GetBytes(context.Request.Headers[BackupServiceClient.ApiTokenHeader].ToString());
        if (apiToken.Length == 0 || !CryptographicOperations.FixedTimeEquals(supplied, apiToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    await next();
});

var backups = app.MapGroup("/internal/backups");
backups.MapPost("/jobs", async (CreateBackupJobRequest request, BackupJobs jobs, CancellationToken ct) =>
{
    try
    {
        return Accepted(await jobs.QueueBackupAsync(request, ct));
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
backups.MapGet("/jobs", (BackupJobs jobs) => jobs.List());
backups.MapGet("/jobs/{jobId:guid}", (Guid jobId, BackupJobs jobs) =>
    jobs.Get(jobId) is { } job ? Results.Ok(job) : Results.NotFound());
backups.MapGet("/backups", async (BackupRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.ReadAsync(ct)));
backups.MapPost("/verify", async (VerifyBackupRequest request, BackupJobs jobs, CancellationToken ct) =>
    Accepted(await jobs.QueueVerifyAsync(request, ct)));

var liveness = new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("live") };
app.MapHealthChecks("/health", liveness);
app.MapHealthChecks("/alive", liveness);

await app.RunAsync();

static IResult Accepted(BackupJobDto job) => Results.Accepted($"/internal/backups/jobs/{job.JobId}", job);
