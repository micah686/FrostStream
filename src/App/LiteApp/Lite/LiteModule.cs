using DataBridge;
using DataBridge.Persistence;
using Shared.Deployment;
using MediaProcessor;
using Scheduler;
using WebAPI;
using Worker;

namespace Lite;

/// <summary>
/// Single-process composition root. Controllers and application handlers remain in shared modules.
/// </summary>
public static class LiteModule
{
    /// <summary>Defaults for the dedicated Lite executable; explicit incompatible settings fail at startup.</summary>
    public static IHostApplicationBuilder ConfigureLiteHost(this IHostApplicationBuilder builder)
    {
        builder.Configuration["Deployment:Mode"] ??= "Lite";
        builder.Configuration["Persistence:Sqlite:Enabled"] ??= "true";
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        builder.Configuration["MediaProcessor:FfmpegPath"] ??= Path.Combine(tools, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        builder.Configuration["MediaProcessor:FfprobePath"] ??= Path.Combine(tools, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Lite ||
            !builder.Configuration.GetValue<bool>("Persistence:Sqlite:Enabled"))
            throw new InvalidOperationException("The Lite host requires Deployment:Mode=Lite and Persistence:Sqlite:Enabled=true.");
        return builder;
    }

    public static IHostApplicationBuilder AddLiteModules(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(LiteModule))) return builder;
        var liteMode = DeploymentOptions.FromConfiguration(builder.Configuration).Mode == DeploymentMode.Lite;
        if (liteMode)
        {
            if (!builder.Configuration.GetValue<bool>("Persistence:Sqlite:Enabled"))
                throw new InvalidOperationException("Lite requires SQLite persistence.");
            builder.AddDataBridgePersistence();
            builder.Services.AddSingleton<ApplicationStartupGate>();
            builder.Services.AddSingleton<LiteReadinessState>();
            builder.Services.AddHealthChecks().AddCheck<LiteReadinessHealthCheck>("lite-readiness");
            builder.Services.AddHostedService<LiteSchemaInitializationService>();
            builder.Services.PostConfigure<HostOptions>(options => options.ServicesStartConcurrently = false);
        }
        // DataBridge selects persistence and transport, then registers reconciliation before consumers.
        builder.AddDataBridgeModule();
        builder.AddMediaProcessorModule();
        builder.AddWorkerModule();
        builder.AddWebAPIModule();
        builder.AddSchedulerModule();
        if (liteMode)
        {
            // Keep this gate before Quartz startup but after ScheduleChangeListener's registration.
            var schedulerStart = builder.Services.Single(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(Scheduler.Services.LiteSchedulerStartupService));
            builder.Services.Remove(schedulerStart);
            builder.Services.AddHostedService<LiteHandlerInitializationService>();
            builder.Services.Add(schedulerStart);
            builder.Services.AddHostedService<LiteReadinessService>();
        }
        return builder;
    }
}
