using Cleipnir.Flows;
using Cleipnir.Flows.AspNet;
using Cleipnir.Flows.PostgresSql;
using DataBridge.Data;
using DataBridge.AudioRenditions;
using DataBridge.Flows;
using DataBridge.MediaStream;
using DataBridge.Metadata;
using DataBridge.Messaging;
using DataBridge.Search;
using DataBridge.Statistics;
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
using Shared.Database;
using Shared.Messaging;
using Shared.Pot;
using Shared.Secrets;
using Shared.Storage;
using Typesense.Setup;

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
                        .MapEnum<LocalStorageProtocol>("local_storage_protocol", "storage")
                        .MapEnum<NetworkStorageProtocol>("network_storage_protocol", "storage")
                        .MapEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider", "storage")
                        .MapEnum<AzureBlobCredentialMode>("azure_blob_credential_mode", "storage")
                        .MapEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode", "storage")
                        .MapEnum<DownloadJobState>("download_job_state", "downloads")
                        .MapEnum<FailureKind>("failure_kind", "downloads")
                        .MapEnum<IngestOrigin>("ingest_origin", "media")
                        .MapEnum<AudioRenditionFormat>("audio_rendition_format", "media")
                        .MapEnum<AudioRenditionStatus>("audio_rendition_status", "media")
                        .MapEnum<LocalImportStatus>("local_import_status", "imports")
                        .MapEnum<PlaylistState>("playlist_state", "playlists"))
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
        builder.Services.AddNatsTopologySource<PlaylistTopology>();
        builder.Services.AddNatsTopologySource<BackgroundJobsTopology>();
        builder.Services.AddNatsTopologySource<LocalImportTopology>();
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
        builder.Services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            return dataSourceBuilder.Build();
        });
        builder.Services.AddScoped<IDownloadJobsRepository, DownloadJobsRepository>();
        builder.Services.AddScoped<ILocalImportRepository, LocalImportRepository>();
        builder.Services.AddScoped<IMetadataRepository, MetadataRepository>();
        builder.Services.AddScoped<IMetadataReadService, MetadataReadService>();
        builder.Services.AddScoped<IStatisticsReadService, StatisticsReadService>();
        builder.Services.AddScoped<IMediaStreamReadService, MediaStreamReadService>();
        builder.Services.AddScoped<IAudioRenditionRepository, AudioRenditionRepository>();
        builder.Services.AddScoped<IPlaylistsRepository, PlaylistsRepository>();
        builder.Services.AddScoped<IUserPlaylistsRepository, UserPlaylistsRepository>();
        builder.Services.AddScoped<IUserNotesRepository, UserNotesRepository>();
        builder.Services.AddScoped<IOptionPresetsRepository, OptionPresetsRepository>();
        builder.Services.AddScoped<IDownloadConfigSetsRepository, DownloadConfigSetsRepository>();
        builder.Services.AddScoped<IScheduledTasksRepository, ScheduledTasksRepository>();
        builder.Services.AddScoped<ICreatorDiscoveryRepository, CreatorDiscoveryRepository>();
        builder.Services.AddSingleton<OrphanMetadataCleanupExecutor>();
        builder.Services.AddSingleton<MediaDeleteExecutor>();
        builder.Services.AddSingleton<WatchedItemAutoDeleteExecutor>();
        builder.Services.AddSingleton<DownloadSlotCoordinator>();

        builder.Services.AddTypesenseClient(config =>
        {
            config.Nodes = [BuildTypesenseNode(builder.Configuration)];
            config.ApiKey = builder.Configuration["Typesense:ApiKey"] ?? "froststream-dev-key";
        });
        builder.Services.AddSingleton<ITypesenseIndexService, TypesenseIndexService>();
        builder.Services.AddSingleton<IMediaDocumentQuery, MediaDocumentQuery>();
        builder.Services.AddSingleton<IMetadataRebuildCoordinator, MetadataRebuildCoordinator>();

        builder.Services.AddHostedService<TypesenseStartupService>();
        builder.Services.AddHostedService<SingleUserOwnerSeederService>();
        builder.Services.AddHostedService<UserSessionConsumerService>();
        builder.Services.AddHostedService<CookieProfileConsumerService>();
        builder.Services.AddHostedService<TypesenseSyncConsumerService>();
        builder.Services.AddHostedService<MetadataListConsumerService>();
        builder.Services.AddHostedService<MetadataSearchConsumerService>();
        builder.Services.AddHostedService<MetadataCommentsConsumerService>();
        builder.Services.AddHostedService<MetadataCaptionsConsumerService>();
        builder.Services.AddHostedService<UnifiedSearchConsumerService>();
        builder.Services.AddHostedService<MetadataRebuildConsumerService>();

        builder.Services.AddHostedService<StorageCrudConsumerService>();
        builder.Services.AddHostedService<OptionPresetCrudConsumerService>();
        builder.Services.AddHostedService<DownloadConfigSetConsumerService>();
        builder.Services.AddHostedService<ScheduleCrudConsumerService>();
        builder.Services.AddHostedService<CreatorDiscoveryConsumerService>();
        builder.Services.AddHostedService<WatchStateConsumerService>();
        builder.Services.AddHostedService<WatchedAutoDeleteAdminConsumerService>();
        builder.Services.AddHostedService<OrphanCleanupAdminConsumerService>();
        builder.Services.AddHostedService<OrphanMetadataCleanupConsumerService>();
        builder.Services.AddHostedService<FilesystemRescanConsumerService>();
        builder.Services.AddHostedService<BackgroundJobConsumerService>();
        builder.Services.AddHostedService<DownloadSlotCoordinator>(p => p.GetRequiredService<DownloadSlotCoordinator>());
        builder.Services.AddHostedService<DownloadAdminConsumerService>();
        builder.Services.AddHostedService<DownloadRequestedIngressService>();
        builder.Services.AddHostedService<ProviderHaltRetryService>();
        builder.Services.AddHostedService<DownloadEventsConsumerService>();
        builder.Services.AddHostedService<LocalMediaImportRequestedIngressService>();
        builder.Services.AddHostedService<LocalImportEventsConsumerService>();
        builder.Services.AddHostedService<PlaylistRequestedIngressService>();
        builder.Services.AddHostedService<PlaylistEventsConsumerService>();
        builder.Services.AddHostedService<PlaylistQueryConsumerService>();
        builder.Services.AddHostedService<UserPlaylistConsumerService>();
        builder.Services.AddHostedService<UserNoteConsumerService>();
        builder.Services.AddHostedService<MetadataQueryConsumerService>();
        builder.Services.AddHostedService<StatisticsQueryConsumerService>();
        builder.Services.AddHostedService<MediaStreamQueryConsumerService>();
        builder.Services.AddHostedService<AudioRenditionConsumerService>();
        builder.Services.AddHostedService<MediaDeleteConsumerService>();

        // POT broker role: answers pot.request over NATS from a nearby bgutil provider. No-ops unless
        // PotBroker:Enabled is set, so this is inert on deployments without a co-located provider.
        builder.Services.AddPotBroker(builder.Configuration);

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

    private static Node BuildTypesenseNode(IConfiguration configuration)
    {
        var url = configuration["Typesense:Url"] ?? configuration.GetConnectionString("typesense");
        if (!string.IsNullOrWhiteSpace(url))
        {
            var uri = new Uri(url, UriKind.Absolute);
            var port = uri.IsDefaultPort
                ? (uri.Scheme == Uri.UriSchemeHttps ? "443" : "80")
                : uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var additionalPath = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
            return new Node(uri.Host, port, uri.Scheme, additionalPath);
        }

        return new Node(
            configuration["Typesense:Host"] ?? "localhost",
            configuration["Typesense:Port"] ?? "8108",
            configuration["Typesense:Protocol"] ?? "http");
    }
}
