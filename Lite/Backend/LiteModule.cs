using FrostStream.Lite.Infrastructure;
using FrostStream.Lite.Infrastructure.Search;
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
        builder.Configuration["MediaProcessor:MaxConcurrentProcesses"] ??= "1";
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        builder.Configuration["MediaProcessor:FfmpegPath"] ??= Path.Combine(tools, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        builder.Configuration["MediaProcessor:FfprobePath"] ??= Path.Combine(tools, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Lite)
            throw new InvalidOperationException("The Lite host requires Deployment:Mode=Lite.");
        PersistenceOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
        return builder;
    }

    public static IHostApplicationBuilder AddLiteModules(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(LiteModule))) return builder;
        builder.AddLitePersistence();
        builder.AddLiteMessaging();
        builder.AddLiteSecretStore();
        builder.AddLiteBackupClient();
        builder.AddLiteStagedStorage();
        builder.AddLiteIdentity();
        builder.Services.AddSingleton<ApplicationStartupGate>();
        builder.Services.AddSingleton<LiteReadinessState>();
        builder.Services.AddHealthChecks().AddCheck<LiteReadinessHealthCheck>("lite-readiness");
        builder.Services.AddSingleton<FrostStream.Lite.Infrastructure.Identity.LocalAdministratorSeeder>();
        builder.Services.AddSingleton(new ApplicationInitializationStep("local owner", 30,
            (sp, ct) => sp.GetRequiredService<FrostStream.Lite.Infrastructure.Identity.LocalAdministratorSeeder>().InitializeAsync(ct)));
        builder.AddDataBridgeModule();
        builder.Services.AddSingleton<MediaProcessor.Storage.IMediaProcessorStorageClient, MediaProcessor.Storage.LocalMediaProcessorStorageClient>();
        builder.AddMediaProcessorModule();
        builder.AddWorkerModule();
        builder.AddWebAPIModule();
        builder.AddSchedulerModule();
        builder.AddLuceneSearch();
        builder.Services.AddHostedService<LiteRuntimeLifecycle>();
        return builder;
    }
}
