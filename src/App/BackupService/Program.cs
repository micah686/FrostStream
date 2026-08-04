using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BackupService;
using BackupService.PgBackRest;
using Conduit.NATS;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Backups;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOptions<BackupServiceOptions>()
    .Bind(builder.Configuration.GetSection(BackupServiceOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Directory), "Backup:Directory is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Stanza), "Backup:Stanza is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.PgDataPath), "Backup:PgDataPath is required.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BackupServiceOptions>>().Value);
builder.Services.AddSingleton<BackupJobStore>();
builder.Services.AddSingleton<PgBackRestRunner>();
builder.Services.AddSingleton<DeepVerifyRunner>();
builder.Services.AddSingleton<PostgresStateProbe>();
builder.Services.AddSingleton<OpenBaoPairing>();
builder.Services.AddSingleton<BackupRepositoryReader>();
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupCoordinator>());
builder.Services.AddHostedService<StanzaStartupService>();
builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
builder.Services.AddSingleton<IBackgroundRunReporter>(sp => new BackgroundRunReporter(
    sp.GetRequiredService<IMessageBus>(),
    sp.GetRequiredService<IClock>(),
    "backupservice",
    sp.GetService<ILogger<BackgroundRunReporter>>()));

// Outbound only (run reporting); commands arrive over REST, so no topology provisioning and no
// consumers. NATS being down must never prevent startup — standalone restore mode runs with the
// entire stack stopped. EnableTopologyProvisioning defaults to true and, with no ITopologySource
// registered, would still block host startup waiting to connect and then crash the whole process
// (Kestrel included) once TotalStartupTimeout elapses; both must be off. BackgroundRunReporter
// already swallows publish failures at call time, so no startup check is needed at all.
builder.AddNats("nats", options =>
{
    options.EnableTopologyProvisioning = false;
    options.ValidateConnectionOnStart = false;
});

// The break-glass restore wizard: cookie-gated Blazor Server on the second Kestrel port.
// Cookie encryption keys live under the backup root: the default ($HOME/.aspnet) would land
// inside the shared postgres data volume, and keys should survive container recreation.
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
    builder.Configuration.GetSection(BackupServiceOptions.SectionName)["Directory"]
    ?? Path.Combine(AppContext.BaseDirectory, "backups"),
    ".dataprotection")));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
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

var app = builder.Build();
var serviceOptions = app.Services.GetRequiredService<BackupServiceOptions>();

// Port guard: the restore-UI port is host-published, so the unauthenticated /internal surface
// must never be reachable through it.
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == serviceOptions.RestoreUiPort
        && context.Request.Path.StartsWithSegments("/internal"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/internal/backups/jobs", async (
    CreateBackupJobRequest request,
    BackupCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    try
    {
        var job = await coordinator.QueueBackupAsync(
            request.Name,
            request.Type,
            request.Scheduled,
            request.ScheduleKey,
            request.IdempotencyKey,
            cancellationToken);
        return Results.Accepted($"/internal/backups/jobs/{job.JobId}", ToDto(job, []));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/internal/backups/jobs", (BackupJobStore store) =>
    store.List().Select(job => ToDto(job, [])));
app.MapGet("/internal/backups/jobs/{jobId:guid}", (Guid jobId, BackupJobStore store, BackupCoordinator coordinator) =>
    store.Get(jobId) is { } job
        ? Results.Ok(ToDto(job, coordinator.GetProgress(jobId)))
        : Results.NotFound());
app.MapGet("/internal/backups/backups", async (BackupRepositoryReader reader, CancellationToken cancellationToken) =>
    Results.Ok(await reader.ReadAsync(cancellationToken)));
app.MapPost("/internal/backups/verify", async (
    VerifyBackupRequest request,
    BackupCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var job = await coordinator.QueueVerifyAsync(request.Label, request.Deep, cancellationToken);
    return Results.Accepted($"/internal/backups/jobs/{job.JobId}", ToDto(job, []));
});

// Token exchange for the restore wizard. The token is the credential itself, so antiforgery
// adds nothing here; a constant-time comparison prevents timing probes.
app.MapPost("/restore-ui/auth", async (HttpContext context, BackupServiceOptions options) =>
{
    var form = await context.Request.ReadFormAsync();
    var supplied = Encoding.UTF8.GetBytes(form["token"].ToString());
    var expected = Encoding.UTF8.GetBytes(options.RestoreUiToken ?? "");
    if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        return Results.Redirect("/login?failed=1");

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "restore-operator")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<BackupService.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health", new HealthCheckOptions());
app.MapDefaultEndpoints();

await app.RunAsync();

static BackupJobDto ToDto(BackupJobRecord job, IReadOnlyList<string> progress)
    => new(
        job.JobId,
        job.Kind,
        job.Type,
        job.Status,
        job.Name,
        job.Label,
        job.ErrorMessage,
        job.CreatedAt,
        job.CompletedAt,
        progress);
