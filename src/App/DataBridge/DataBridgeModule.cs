using Shared.Messaging.Adapters;
using Cleipnir.Flows;
using Cleipnir.Flows.AspNet;
using DataBridge.Persistence.Workflows;
using DataBridge.Data;
using DataBridge.Persistence;
using DataBridge.AudioRenditions;
using DataBridge.Renditions;
using DataBridge.StreamRenditions;
using DataBridge.Flows;
using DataBridge.LiveChat;
using DataBridge.MediaStream;
using DataBridge.Metadata;
using DataBridge.Messaging;
using DataBridge.Search;
using DataBridge.Statistics;
using Conduit.NATS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NATS.Client.Core;
using NodaTime;
using Npgsql;
using Shared.LiveChat;
using Shared.Messaging;
using Shared.Pot;
using Shared.Secrets;
using Shared.Storage;
using Typesense.Setup;

using Shared.Deployment;

namespace DataBridge;

public static class DataBridgeModule
{
    public static IHostApplicationBuilder AddDataBridgeModule(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(DataBridgeModule))) return builder;
        builder.AddDeployment();
        builder.AddDataBridgePersistence();
        var persistence = PersistenceOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddApplicationTransport(builder.Configuration);
        var liteMode = DeploymentOptions.FromConfiguration(builder.Configuration).Mode == DeploymentMode.Lite;

        var connectionString = builder.Configuration.GetConnectionString("froststreamdb")
            ?? "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres";
        var natsUrl = builder.Configuration.GetConnectionString("nats")
            ?? builder.Configuration["NATS:Url"]
            ?? "nats://localhost:24040";
        var natsAuth = BuildNatsAuth(builder.Configuration);

        builder.Services.AddModuleNats(options =>
        {
            options.Url = natsUrl;
            options.AuthOpts = natsAuth;
            // Provisions every ITopologySource registered below at startup.
            options.EnableTopologyProvisioning = true;
        });

        builder.Services.AddModuleTopology<DownloadTopology>();
        builder.Services.AddModuleTopology<ArtifactStorageTopology>();
        builder.Services.AddModuleTopology<PlaylistTopology>();
        builder.Services.AddModuleTopology<BackgroundJobsTopology>();
        builder.Services.AddModuleTopology<LocalImportTopology>();
        builder.Services.AddModuleTopology<AuthSessionsTopology>();
        builder.Services.AddApplicationSecretStore(builder.Configuration);
        builder.Services.AddFrostStreamStorage();

        if (liteMode)
        {
            builder.Services.AddHostedService<SingleUserOwnerSeederService>();
            builder.Services.AddHostedService<DownloadFlowStartupService>();
        }
        builder.Services.AddFlows(c => c
            .UsePersistenceStore(persistence, connectionString)
            // Cleipnir's DefaultSerializer has no NodaTime support and collapses every Instant in a
            // persisted message/effect to the Unix epoch. Swap in a NodaTime-aware serializer so
            // dates (OccurredAt, metadata scrape/release dates, …) survive the flow store round-trip.
            .WithOptions(new Options(serializer: new NodaTimeFlowSerializer()))
            // Let executing effects leave DI cleanly. On the next DataBridge generation the V2
            // startup gate still deletes every recorded run/group flow and marks begun work
            // Interrupted; graceful host teardown never implies automatic resume.
            .GracefulShutdown(enable: true)
            .RegisterFlowsAutomatically(typeof(DataBridgeModule).Assembly));
        // Host construction may instantiate flows; their watchdogs are gated by the store until
        // reconciliation commits. Every application consumer starts after this blocking service.
        builder.Services.PostConfigure<HostOptions>(o => o.ServicesStartConcurrently = false);
        if (!liteMode) builder.Services.AddHostedService<DownloadFlowStartupService>();

        builder.Services.AddSingleton<IDownloadJobStateNotifier, DownloadJobStateNotifier>();
        builder.Services.AddScoped<IDownloadJobsRepository, DownloadJobsRepository>();
        builder.Services.AddScoped<IDownloadFlowV2Repository, DownloadFlowV2Repository>();
        builder.Services.AddScoped<IImportSessionRepository, ImportSessionRepository>();
        builder.Services.AddScoped<IMetadataRepository, MetadataRepository>();
        builder.Services.AddScoped<IMetadataReadService, MetadataReadService>();
        builder.Services.AddScoped<IStatisticsReadService, StatisticsReadService>();
        builder.Services.AddScoped<IMediaStreamReadService, MediaStreamReadService>();
        builder.Services.AddScoped<IMediaThumbnailReadService, MediaThumbnailReadService>();
        builder.Services.AddScoped<IMediaThumbnailGenerationService, MediaThumbnailGenerationService>();
        builder.Services.AddScoped<IMediaCaptionReadService, MediaCaptionReadService>();
        builder.Services.AddScoped<IAccountAssetReadService, AccountAssetReadService>();
        builder.Services.AddScoped<IAudioRenditionRepository, AudioRenditionRepository>();
        builder.Services.AddScoped<IMediaEncodingStatusRepository, MediaEncodingStatusRepository>();
        builder.Services.AddScoped<IStreamRenditionRepository, StreamRenditionRepository>();
        builder.Services.AddScoped<IRenditionQueueRepository, RenditionQueueRepository>();
        builder.Services.AddScoped<IPlaylistsRepository, PlaylistsRepository>();
        builder.Services.AddScoped<IUserPlaylistsRepository, UserPlaylistsRepository>();
        builder.Services.AddScoped<IUserNotesRepository, UserNotesRepository>();
        builder.Services.AddScoped<IOptionPresetsRepository, OptionPresetsRepository>();
        builder.Services.AddScoped<IDownloadConfigSetsRepository, DownloadConfigSetsRepository>();
        builder.Services.AddScoped<IScheduledTasksRepository, ScheduledTasksRepository>();
        builder.Services.AddScoped<ICreatorDiscoveryRepository, CreatorDiscoveryRepository>();
        builder.Services.AddSingleton<MediaDeleteExecutor>();
        builder.Services.AddSingleton<AccessPolicyExecutor>();
        builder.Services.Configure<MediaAccessOptions>(
            builder.Configuration.GetSection(MediaAccessOptions.SectionName));
        builder.Services.Configure<LiveChatOptions>(
            builder.Configuration.GetSection(LiveChatOptions.SectionName));
        // Optional live-chat replay store. Nothing ClickHouse-related is registered when the
        // feature is disabled — the flag gates the whole dependency, not individual calls.
        if (builder.Configuration.GetSection(LiveChatOptions.SectionName).GetValue<bool>("Enabled"))
        {
            builder.Services.AddSingleton<ClickHouseAccess>();
            builder.Services.AddScoped<LiveChatIngestService>();
            // Schema first: IHostedService instances start in registration order, and the
            // consumers below must never race the DDL.
            builder.Services.AddHostedService<ClickHouseSchemaService>();
            builder.Services.AddHostedService<LiveChatIngestConsumerService>();
            builder.Services.AddHostedService<LiveChatBackfillConsumerService>();
            builder.Services.AddHostedService<LiveChatQueryConsumerService>();
        }
        builder.Services.AddSingleton<DownloadFlowStartupState>();
        builder.Services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
        builder.Services.AddKeyedSingleton<IBackgroundRunReporter>("databridge", (sp, _) => new BackgroundRunReporter(
            sp.GetRequiredService<FrostStream.ApplicationContracts.IMessageBus>(),
            sp.GetRequiredService<IClock>(),
            "databridge",
            sp.GetService<Microsoft.Extensions.Logging.ILogger<BackgroundRunReporter>>()));
        builder.Services.AddSingleton<ImportSessionRequestReplyService>();
        builder.Services.AddSingleton<WorkerRegistryConsumerService>();
        builder.Services.AddSingleton<LocalImportItemV2Flows>();
        builder.Services.AddSingleton<IDownloadHistoryPurger, DownloadHistoryPurger>();
        builder.Services.AddSingleton<IImportSessionPurger, ImportSessionPurger>();

        builder.Services.AddTypesenseClient(config =>
        {
            config.Nodes = [BuildTypesenseNode(builder.Configuration)];
            config.ApiKey = builder.Configuration["Typesense:ApiKey"] ?? "froststream-dev-key";
        });
        builder.Services.AddSingleton<ITypesenseIndexService, TypesenseIndexService>();
        builder.Services.AddSingleton<IMediaDocumentQuery, MediaDocumentQuery>();
        builder.Services.AddSingleton<CaptionDocumentHydrator>();
        builder.Services.AddSingleton<IMetadataRebuildCoordinator, MetadataRebuildCoordinator>();

        builder.Services.AddHostedService<TypesenseStartupService>();
        if (!liteMode) builder.Services.AddHostedService<SingleUserOwnerSeederService>();
        builder.Services.AddHostedService<UserSessionConsumerService>();
        builder.Services.AddHostedService<NotificationPreferencesConsumerService>();
        builder.Services.AddHostedService<CookieProfileConsumerService>();
        builder.Services.AddHostedService<TypesenseSyncConsumerService>();
        builder.Services.AddHostedService<MetadataListConsumerService>();
        builder.Services.AddHostedService<MetadataCommentsConsumerService>();
        builder.Services.AddHostedService<MetadataCaptionsConsumerService>();
        builder.Services.AddHostedService<UnifiedSearchConsumerService>();

        builder.Services.AddHostedService<StorageCrudConsumerService>();
        builder.Services.AddHostedService<OptionPresetCrudConsumerService>();
        builder.Services.AddHostedService<DownloadConfigSetConsumerService>();
        builder.Services.AddHostedService<ScheduleCrudConsumerService>();
        builder.Services.AddHostedService<CreatorDiscoveryConsumerService>();
        builder.Services.AddHostedService<WatchStateConsumerService>();
        builder.Services.AddHostedService<BackgroundJobConsumerService>();
        builder.Services.AddHostedService<DownloadAdminConsumerService>();
        builder.Services.AddHostedService<DownloadLeaseMonitorService>();
        builder.Services.AddHostedService<DownloadQueueConsumerService>();
        builder.Services.AddHostedService<DownloadProgressPersistenceService>();
        builder.Services.AddHostedService<DownloadGroupRequestedIngressService>();
        builder.Services.AddHostedService<DownloadRequestedIngressService>();
        builder.Services.AddHostedService<DownloadEventsConsumerService>();
        builder.Services.AddHostedService<DownloadGroupExpansionEventsConsumerService>();
        builder.Services.AddHostedService<DownloadStageTelemetryConsumerService>();
        builder.Services.AddHostedService<LocalImportEventsConsumerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ImportSessionRequestReplyService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerRegistryConsumerService>());
        builder.Services.AddHostedService<ImportSessionProbeEventsConsumerService>();
        builder.Services.AddHostedService<ImportDispatcherService>();
        builder.Services.AddHostedService<PlaylistEventsConsumerService>();
        builder.Services.AddHostedService<PlaylistQueryConsumerService>();
        builder.Services.AddHostedService<UserPlaylistConsumerService>();
        builder.Services.AddHostedService<UserNoteConsumerService>();
        builder.Services.AddHostedService<MetadataQueryConsumerService>();
        builder.Services.AddHostedService<StatisticsQueryConsumerService>();
        builder.Services.AddHostedService<MediaStreamQueryConsumerService>();
        builder.Services.AddHostedService<MediaThumbnailGenerationConsumerService>();
        builder.Services.AddHostedService<AudioRenditionConsumerService>();
        builder.Services.AddHostedService<MediaEncodingStatusConsumerService>();
        builder.Services.AddHostedService<StreamRenditionConsumerService>();
        builder.Services.AddHostedService<RenditionQueueConsumerService>();
        builder.Services.AddHostedService<MediaDeleteConsumerService>();
        builder.Services.AddHostedService<AccessPolicyConsumerService>();

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


        return builder;
    }

    public static void InitializeDataBridge(this IHost app, CancellationToken cancellationToken = default)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IApplicationSchemaInitializer>().Initialize(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (scope.ServiceProvider.GetRequiredService<PersistenceOptions>().Provider == PersistenceProvider.Sqlite)
            scope.ServiceProvider.GetRequiredService<SqliteFunctionStore>().Initialize().GetAwaiter().GetResult();
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
            configuration["Typesense:Port"] ?? "24010",
            configuration["Typesense:Protocol"] ?? "http");
    }
}
