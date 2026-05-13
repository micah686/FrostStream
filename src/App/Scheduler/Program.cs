using FlySwattr.NATS.Extensions;
using FlySwattr.NATS.Topology.Extensions;
using NodaTime;
using Quartz;
using Scheduler.ChannelTasks;
using Scheduler.Databridge;
using Scheduler.MaintenanceTasks;
using Scheduler.Messaging;
using Scheduler.Options;
using Scheduler.Scheduling;
using Scheduler.Services;
using Scheduler.Triggers;
using Shared.Messaging;

namespace Scheduler;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.Configure<QuartzDashboardOptions>(
            builder.Configuration.GetSection(QuartzDashboardOptions.SectionName));
        builder.Services.Configure<NatsOptions>(
            builder.Configuration.GetSection(NatsOptions.SectionName));
        builder.Services.Configure<SchedulerQuartzOptions>(
            builder.Configuration.GetSection(SchedulerQuartzOptions.SectionName));
        builder.Services.Configure<ChannelJobOptions>(
            builder.Configuration.GetSection(ChannelJobOptions.SectionName));
        builder.Services.Configure<MaintenanceJobOptions>(
            builder.Configuration.GetSection(MaintenanceJobOptions.SectionName));

        builder.Services.AddEnterpriseNATSMessaging(options =>
        {
            options.Core.Url = NatsConnectionFactory.GetUrl(builder.Configuration);
            options.Core.NatsAuth = NatsConnectionFactory.BuildAuth(builder.Configuration);
            options.EnableTopologyProvisioning = true;
            options.EnablePayloadOffloading = false;
            options.EnableResilience = false;
            options.EnableCaching = false;
            options.EnableDistributedLock = false;
            options.EnableDlqAdvisoryListener = false;
        });
        builder.Services.AddNatsTopologySource<BackgroundJobsTopology>();
        builder.Services.AddSingleton<INatsMessagePublisher, NatsMessagePublisher>();
        builder.Services.AddSingleton<INatsRequestClient, NatsRequestClient>();
        builder.Services.AddSingleton<IDatabridgeClient, DatabridgeClient>();

        builder.Services.AddQuartz(q =>
        {
            q.SchedulerName = "FrostStream Scheduler";
            q.SchedulerId = "froststream-scheduler";
            q.UseSimpleTypeLoader();
            q.UseInMemoryStore();
            q.UseDefaultThreadPool(threadPool =>
            {
                threadPool.MaxConcurrency = builder.Configuration.GetValue("Quartz:MaxConcurrency", 10);
            });
        });
        builder.Services.AddQuartzHostedService(options =>
        {
            options.WaitForJobsToComplete = true;
        });

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<IQuartzJobRegistrar, QuartzJobRegistrar>();

        builder.Services.AddTransient<OrphanMetadataCleanupTriggerJob>();
        builder.Services.AddTransient<Jobs.ChannelUpdateCheckJob>();
        builder.Services.AddTransient<Jobs.ChannelAssetRefreshJob>();
        builder.Services.AddTransient<Jobs.ChannelMediaListJob>();
        builder.Services.AddTransient<Jobs.StaleDatabaseCleanupJob>();
        builder.Services.AddTransient<Jobs.DatabaseMaintenanceJob>();
        builder.Services.AddTransient<Jobs.SearchReindexJob>();
        builder.Services.AddTransient<Jobs.FilesystemRescanJob>();
        builder.Services.AddTransient<Jobs.HeavyDataProcessingScheduleJob>();

        builder.Services.AddSingleton<IChannelUpdateChecker, ChannelUpdateChecker>();
        builder.Services.AddSingleton<IChannelAssetRefresher, ChannelAssetRefresher>();
        builder.Services.AddSingleton<IChannelMediaLister, ChannelMediaLister>();
        builder.Services.AddSingleton<IOrphanMetadataCleanupScheduler, OrphanMetadataCleanupScheduler>();
        builder.Services.AddSingleton<IStaleEntryCleanupScheduler, StaleEntryCleanupScheduler>();
        builder.Services.AddSingleton<IDatabaseMaintenanceScheduler, DatabaseMaintenanceScheduler>();
        builder.Services.AddSingleton<ISearchReindexScheduler, SearchReindexScheduler>();
        builder.Services.AddSingleton<IFilesystemRescanScheduler, FilesystemRescanScheduler>();
        builder.Services.AddSingleton<IHeavyDataProcessingScheduler, HeavyDataProcessingScheduler>();

        builder.Services.AddHostedService<ScheduleHydrationService>();
        builder.Services.AddHostedService<ScheduleChangeListener>();

        var app = builder.Build();
        app.UseCrystalQuartzDashboard();
        app.MapDefaultEndpoints();

        await app.RunAsync();
    }
}
