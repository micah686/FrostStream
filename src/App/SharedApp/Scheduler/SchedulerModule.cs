using Shared.Messaging.Adapters;
using Conduit.NATS;
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

using Shared.Deployment;
using Microsoft.Extensions.Hosting;

namespace Scheduler;

public static class SchedulerModule
{
    public static IHostApplicationBuilder AddSchedulerModule(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(SchedulerModule))) return builder;
        builder.AddDeployment();
        var liteMode = DeploymentOptions.FromConfiguration(builder.Configuration).Mode == DeploymentMode.Lite;
        builder.Services.AddApplicationTransport(builder.Configuration);

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

        builder.Services.AddModuleNats(options =>
        {
            options.Url = NatsConnectionFactory.GetUrl(builder.Configuration);
            options.AuthOpts = NatsConnectionFactory.BuildAuth(builder.Configuration);
            options.EnableTopologyProvisioning = true;
        });
        builder.Services.AddModuleTopology<BackgroundJobsTopology>();
        builder.Services.AddSingleton<INatsMessagePublisher, NatsMessagePublisher>();
        // Scheduled backups are dispatched to BackupService over REST rather than JetStream;
        // resilience + service discovery come from AddServiceDefaults.
        Shared.Backups.ApplicationBackupRegistration.AddApplicationBackupClient(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<INatsRequestClient, NatsRequestClient>();
        builder.Services.AddSingleton<IDatabridgeClient, DatabridgeClient>();

        builder.Services.AddSingleton<BackgroundRunDispatchListener>();

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
            // Every firing announces itself on Jobs > Background before the task publishes its request.
            q.AddJobListener(sp => sp.GetRequiredService<BackgroundRunDispatchListener>());
        });
        if (!liteMode)
            builder.Services.AddQuartzHostedService(options =>
            {
                options.WaitForJobsToComplete = true;
            });

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<IQuartzJobRegistrar, QuartzJobRegistrar>();

        builder.Services.AddTransient<Jobs.ChannelScanRefreshJob>();
        builder.Services.AddTransient<Jobs.ChannelAssetRefreshJob>();
        builder.Services.AddTransient<Jobs.ChannelScanFullJob>();
        builder.Services.AddTransient<Jobs.DatabaseStaleMediaCleanupJob>();
        builder.Services.AddTransient<Jobs.DatabaseMaintenanceJob>();
        builder.Services.AddTransient<Jobs.DatabaseMaintenanceReindexJob>();
        builder.Services.AddTransient<Jobs.SearchReindexJob>();
        builder.Services.AddTransient<Jobs.DownloadHistoryCleanupJob>();
        builder.Services.AddTransient<Jobs.ImportSessionCleanupJob>();
        builder.Services.AddTransient<Jobs.BackupJob>();

        builder.Services.AddSingleton<IChannelScanRefresher, ChannelScanRefresher>();
        builder.Services.AddSingleton<IChannelAssetRefresher, ChannelAssetRefresher>();
        builder.Services.AddSingleton<IChannelScanFullScheduler, ChannelScanFullScheduler>();
        builder.Services.AddSingleton<IStaleEntryCleanupScheduler, StaleEntryCleanupScheduler>();
        builder.Services.AddSingleton<IDatabaseMaintenanceScheduler, DatabaseMaintenanceScheduler>();
        builder.Services.AddSingleton<IDatabaseMaintenanceReindexScheduler, DatabaseMaintenanceReindexScheduler>();
        builder.Services.AddSingleton<ISearchReindexScheduler, SearchReindexScheduler>();
        builder.Services.AddSingleton<IDownloadHistoryCleanupScheduler, DownloadHistoryCleanupScheduler>();
        builder.Services.AddSingleton<IImportSessionCleanupScheduler, ImportSessionCleanupScheduler>();
        builder.Services.AddSingleton<IBackupScheduler, BackupScheduler>();

        if (liteMode)
        {
            // DataBridge's blocking reconciliation and module startup run first. BackgroundService
            // subscription registration is asynchronous on .NET 10, so Quartz also awaits readiness.
            builder.Services.PostConfigure<HostOptions>(options => options.ServicesStartConcurrently = false);
            builder.Services.AddSingleton<ScheduleHydrationService>();
            builder.Services.AddHostedService<ScheduleChangeListener>();
            builder.Services.AddHostedService<LiteSchedulerStartupService>();
        }
        else
        {
            builder.Services.AddHostedService<ScheduleHydrationService>();
            builder.Services.AddHostedService<ScheduleChangeListener>();
        }

        return builder;
    }

    public static WebApplication MapSchedulerModule(this WebApplication app)
    {

        app.UseCrystalQuartzDashboard();
        app.MapDefaultEndpoints();


        return app;
    }

}
