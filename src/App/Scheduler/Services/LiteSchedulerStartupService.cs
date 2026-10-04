using Quartz;
using Shared.Messaging;

namespace Scheduler.Services;

/// <summary>
/// Registered after the application modules: reconciliation completes in their blocking startup
/// services, then subscriptions and SQLite-backed schedule hydration must finish before Quartz runs.
/// </summary>
public sealed class LiteSchedulerStartupService(
    IServiceProvider services,
    ISchedulerFactory schedulerFactory,
    ScheduleHydrationService hydration,
    ILogger<LiteSchedulerStartupService> logger) : IHostedService
{
    private IScheduler? scheduler;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registrations = services.GetServices<IHostedService>()
            .TakeWhile(service => !ReferenceEquals(service, this))
            .OfType<FrostStream.ApplicationContracts.IApplicationHandlerInitialization>().Select(service => service.RegistrationCompleted);
        await Task.WhenAll(registrations).WaitAsync(cancellationToken);
        scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        try
        {
            if (!await hydration.HydrateAsync(cancellationToken))
                throw new InvalidOperationException("Lite cannot start scheduling until persisted schedules have been loaded.");
            await scheduler.Start(cancellationToken);
            logger.LogInformation("Lite scheduling started after handler initialization and schedule hydration.");
        }
        catch
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, cancellationToken: CancellationToken.None);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => scheduler is null ? Task.CompletedTask : scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
}
