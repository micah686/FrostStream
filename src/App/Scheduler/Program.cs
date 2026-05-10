using FlySwattr.NATS.Extensions;
using FlySwattr.NATS.Topology.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NATS.Client.Core;
using NodaTime;
using Quartz;
using Scheduler.Services;
using Scheduler.Triggers;
using Shared.Messaging;

namespace Scheduler;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = false);

        var natsUrl = builder.Configuration.GetConnectionString("nats")
            ?? builder.Configuration["NATS:Url"]
            ?? "nats://localhost:4222";
        var natsAuth = BuildNatsAuth(builder.Configuration);

        builder.Services.AddEnterpriseNATSMessaging(options =>
        {
            options.Core.Url = natsUrl;
            options.Core.NatsAuth = natsAuth;
            // Scheduler is a publisher to FROSTSTREAM_BACKGROUND; it does not consume
            // from JetStream, so topology provisioning is left to DataBridge (whichever
            // service ensures the stream first wins; both Add the same source for safety).
            options.EnableTopologyProvisioning = true;
            options.EnablePayloadOffloading = false;
            options.EnableResilience = false;
            options.EnableCaching = false;
            options.EnableDistributedLock = false;
            options.EnableDlqAdvisoryListener = false;
        });

        builder.Services.AddNatsTopologySource<BackgroundJobsTopology>();

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);

        // Quartz — in-memory store. Source of truth for schedule definitions is
        // DataBridge/Postgres; ScheduleHydrationService rehydrates triggers on startup,
        // ScheduleChangeListener keeps them in sync via fs.schedules.changed events.
        builder.Services.AddQuartz();
        builder.Services.AddQuartzHostedService(opt =>
        {
            opt.WaitForJobsToComplete = true;
        });

        // Trigger jobs are resolved from DI by Quartz's Microsoft DI integration.
        builder.Services.AddTransient<OrphanMetadataCleanupTriggerJob>();

        builder.Services.AddHostedService<ScheduleHydrationService>();
        builder.Services.AddHostedService<ScheduleChangeListener>();

        var app = builder.Build();
        await app.RunAsync();
    }

    private static NatsAuthOpts? BuildNatsAuth(IConfiguration configuration)
    {
        var token = configuration["NATS:Token"];
        if (!string.IsNullOrWhiteSpace(token))
            return new NatsAuthOpts { Token = token };

        var username = configuration["NATS:Username"];
        var password = configuration["NATS:Password"];
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            return new NatsAuthOpts { Username = username, Password = password };

        var credsFile = configuration["NATS:CredsFile"];
        if (!string.IsNullOrWhiteSpace(credsFile))
            return new NatsAuthOpts { CredsFile = credsFile };

        return null;
    }
}
