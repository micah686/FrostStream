using Quartz;
using Scheduler.Options;
using Scheduler.Scheduling;

namespace Scheduler;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.Configure<QuartzDashboardOptions>(
            builder.Configuration.GetSection(QuartzDashboardOptions.SectionName));

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

        var app = builder.Build();
        app.UseCrystalQuartzDashboard();
        app.MapDefaultEndpoints();

        await app.RunAsync();
    }
}
