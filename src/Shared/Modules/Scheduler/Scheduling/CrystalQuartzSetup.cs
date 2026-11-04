using CrystalQuartz.Application;
using CrystalQuartz.AspNetCore;
using Microsoft.Extensions.Options;
using Quartz;
using Scheduler.Options;

namespace Scheduler.Scheduling;

internal static class CrystalQuartzSetup
{
    public static WebApplication UseCrystalQuartzDashboard(this WebApplication app)
    {
        var dashboardOptions = app.Services
            .GetRequiredService<IOptions<QuartzDashboardOptions>>()
            .Value;

        var schedulerFactory = app.Services.GetRequiredService<ISchedulerFactory>();
        var scheduler = schedulerFactory.GetScheduler().GetAwaiter().GetResult();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CrystalQuartz");

        app.UseCrystalQuartz(CreateSchedulerProvider(scheduler), CreateOptions(dashboardOptions, logger));

        return app;
    }

    internal static Func<IScheduler> CreateSchedulerProvider(IScheduler scheduler) => () => scheduler;

    internal static CrystalQuartzOptions CreateOptions(QuartzDashboardOptions options, ILogger logger)
        => new()
        {
            Path = NormalizePath(options.Path),
            ReadOnly = options.ReadOnly,
            LazyInit = options.LazyInit,
            TimelineSpan = TimeSpan.FromMinutes(Math.Max(1, options.TimelineSpanMinutes)),
            OnUnhandledPanelException = CreatePanelExceptionHandler(logger)
        };

    internal static Action<Exception> CreatePanelExceptionHandler(ILogger logger)
        => ex => logger.LogError(ex, "Unhandled CrystalQuartz dashboard exception");

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/quartz";
        }

        return path.StartsWith("/", StringComparison.Ordinal)
            ? path
            : "/" + path;
    }
}
