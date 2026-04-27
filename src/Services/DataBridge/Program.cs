using Cleipnir.Flows;
using Cleipnir.Flows.AspNet;
using Cleipnir.Flows.PostgresSql;
using DataBridge.Data;
using DataBridge.Messaging;
using FlySwattr.NATS.Extensions;
using FlySwattr.NATS.Topology.Extensions;
using FluentMigrator.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NATS.Client.Core;
using NodaTime;
using Npgsql;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;

namespace DataBridge;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        var connectionString = builder.Configuration.GetConnectionString("froststreamdb")
            ?? "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres";
        var natsUrl = builder.Configuration.GetConnectionString("nats")
            ?? builder.Configuration["NATS:Url"]
            ?? "nats://localhost:4222";
        var natsAuth = BuildNatsAuth(builder.Configuration);

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

        builder.Services
            .AddFluentMigratorCore()
            .ConfigureRunner(runnerBuilder => runnerBuilder
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(Program).Assembly).For.Migrations());
        
        builder.Services.AddEnterpriseNATSMessaging(options =>
        {
            options.Core.Url = natsUrl;
            options.Core.NatsAuth = natsAuth;
            // Provisions every ITopologySource registered below at startup.
            options.EnableTopologyProvisioning = true;
            options.EnablePayloadOffloading = false;
            options.EnableResilience = false;
            options.EnableCaching = false;
            options.EnableDistributedLock = false;
            options.EnableDlqAdvisoryListener = false;
        });

        builder.Services.AddNatsTopologySource<DownloadTopology>();
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);

        // Isolate Cleipnir's runtime tables in their own Postgres schema. Cleipnir's
        // PostgresSql store only exposes `tablePrefix`, not a schema option, so we route
        // its DDL/DML into the `cleipnir` schema via Npgsql's Search Path. The schema
        // itself is created by FluentMigrator (M004_CreateCleipnirSchema) which runs
        // before the host starts, so by the time AddFlows resolves its store the schema
        // already exists.
        var cleipnirConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "cleipnir,public"
        }.ConnectionString;

        builder.Services.AddFlows(c => c
            .UsePostgresStore(cleipnirConnectionString)
            .RegisterFlowsAutomatically());

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddScoped<IDownloadJobsRepository, DownloadJobsRepository>();

        builder.Services.AddHostedService<StorageCrudConsumerService>();
        builder.Services.AddHostedService<DownloadRequestedIngressService>();
        builder.Services.AddHostedService<DownloadEventsConsumerService>();

        // Force ConsoleLifetime so Ctrl+C / SIGTERM triggers StopAsync on hosted services
        builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();
        builder.Services.Configure<ConsoleLifetimeOptions>(o =>
        {
            // set true to hide "Application started/stopped" messages
            o.SuppressStatusMessages = false;
        });
        

        var app = builder.Build();
        
        using (var scope = app.Services.CreateScope())
        {
            var migrationRunner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            migrationRunner.MigrateUp();
        }

        await app.RunAsync();  // waits until Ctrl+C or SIGTERM, then calls StopAsync() gracefully
    }

    private static NatsAuthOpts? BuildNatsAuth(IConfiguration configuration)
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
}
