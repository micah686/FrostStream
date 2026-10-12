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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Shared.LiveChat;
using Shared.Messaging;
using Shared.Pot;
using Shared.Storage;


using Shared.Deployment;

namespace DataBridge;

public static class DataBridgeModule
{
    public static IHostApplicationBuilder AddDataBridgeModule(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(DataBridgeModule))) return builder;
        builder.AddDeployment();
        builder.Services.AddFrostStreamStorage();

        builder.Services.AddFlows(c => c
            .UseApplicationStore()
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
        builder.Services.AddSingleton<DownloadFlowStartupService>();
        builder.Services.AddSingleton(new ApplicationInitializationStep("download reconciliation", 40,
            (sp, ct) => sp.GetRequiredService<DownloadFlowStartupService>().StartAsync(ct)));

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
        builder.Services.AddSingleton<SearchWriterGate>();
        builder.Services.AddSingleton<SearchStatus>();
        builder.Services.AddHostedService<SearchSyncConsumerService>();
        builder.Services.AddHostedService<PendingIndexWorker>();
        builder.Services.AddSingleton<IMediaDocumentQuery, MediaDocumentQuery>();
        builder.Services.AddSingleton<CaptionDocumentHydrator>();
        builder.Services.AddSingleton<IMetadataRebuildCoordinator, MetadataRebuildCoordinator>();
        builder.Services.AddHostedService<MetadataListConsumerService>();
        builder.Services.AddHostedService<MetadataCommentsConsumerService>();
        builder.Services.AddHostedService<MetadataCaptionsConsumerService>();
        builder.Services.AddHostedService<UnifiedSearchConsumerService>();
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
            builder.Services.AddSingleton<ClickHouseSchemaService>();
            builder.Services.AddSingleton(new ApplicationInitializationStep("live-chat schema", 15,
                (sp, ct) => sp.GetRequiredService<ClickHouseSchemaService>().StartAsync(ct)));
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

        builder.Services.AddHostedService<UserSessionConsumerService>();
        builder.Services.AddHostedService<NotificationPreferencesConsumerService>();
        builder.Services.AddHostedService<CookieProfileConsumerService>();

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

        return builder;
    }

    public static void InitializeDataBridge(this IHost app, CancellationToken cancellationToken = default)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IApplicationSchemaInitializer>().Initialize(cancellationToken);
        scope.ServiceProvider.GetService<DataBridge.Persistence.Indexing.IIndexSchemaInitializer>()?.Initialize();
        cancellationToken.ThrowIfCancellationRequested();
        scope.ServiceProvider.GetRequiredService<IWorkflowStoreFactory>().Create().Initialize().GetAwaiter().GetResult();
    }

}
