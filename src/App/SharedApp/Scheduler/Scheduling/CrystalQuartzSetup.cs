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

        app.UseCrystalQuartz(
            () => scheduler,
            new CrystalQuartzOptions
            {
                Path = NormalizePath(dashboardOptions.Path),
                ReadOnly = dashboardOptions.ReadOnly,
                LazyInit = dashboardOptions.LazyInit,
                TimelineSpan = TimeSpan.FromMinutes(Math.Max(1, dashboardOptions.TimelineSpanMinutes)),
                OnUnhandledPanelException = ex => logger.LogError(ex, "Unhandled CrystalQuartz dashboard exception")
            });

        return app;
    }

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
