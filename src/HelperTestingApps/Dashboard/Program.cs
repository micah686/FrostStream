using Dashboard.Components;
using Dashboard.Services;
using DataBridge.Data;
using FlySwattr.NATS.Extensions;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NodaTime.Serialization.SystemTextJson;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("froststreamdb")
    ?? "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres";
var natsUrl = builder.Configuration.GetConnectionString("nats")
    ?? builder.Configuration["NATS:Url"]
    ?? "nats://localhost:4222";

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<DataBridgeDbContext>(options =>
    options.UseNpgsql(
            connectionString,
            npgsqlOptions => npgsqlOptions
                .UseNodaTime()
                .MapEnum<LocalStorageProtocol>("local_storage_protocol")
                .MapEnum<NetworkStorageProtocol>("network_storage_protocol")
                .MapEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider")
                .MapEnum<AzureBlobCredentialMode>("azure_blob_credential_mode")
                .MapEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode")
                .MapEnum<DownloadJobState>("download_job_state")
                .MapEnum<FailureKind>("failure_kind"))
        .UseSnakeCaseNamingConvention());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddEnterpriseNATSMessaging(options =>
{
    options.Core.Url = natsUrl;
    options.Core.NatsAuth = BuildNatsAuth(builder.Configuration);
    options.EnableTopologyProvisioning = false;
    options.EnablePayloadOffloading = false;
    options.EnableResilience = false;
    options.EnableCaching = false;
    options.EnableDistributedLock = false;
    options.EnableDlqAdvisoryListener = false;
});

builder.Services.AddScoped<JobQueryService>();
builder.Services.AddSingleton<JobsDashboardState>();
builder.Services.AddHostedService<NatsJobActivityListener>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

app.Run();

static NatsAuthOpts? BuildNatsAuth(IConfiguration configuration)
{
    var token = configuration["NATS:Token"];
    if (!string.IsNullOrWhiteSpace(token))
    {
        return new NatsAuthOpts { Token = token };
    }

    var username = configuration["NATS:Username"];
    var password = configuration["NATS:Password"];
    if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
    {
        return new NatsAuthOpts
        {
            Username = username,
            Password = password
        };
    }

    var credsFile = configuration["NATS:CredsFile"];
    if (!string.IsNullOrWhiteSpace(credsFile))
    {
        return new NatsAuthOpts { CredsFile = credsFile };
    }

    return null;
}
