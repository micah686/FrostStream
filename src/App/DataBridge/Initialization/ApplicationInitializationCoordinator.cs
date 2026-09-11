using Microsoft.Extensions.Logging;

namespace DataBridge.Initialization;

public sealed class ApplicationInitializationCoordinator(
    IEnumerable<IApplicationInitializationStep> steps,
    IInitializationStateStore stateStore,
    ILogger<ApplicationInitializationCoordinator> logger)
{
    public const int CurrentVersion = 1;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var ordered = OrderSteps(steps);
        // A new attempt invalidates an older completion marker up front. If this process dies or
        // any later step fails, ordinary startup cannot mistake partially reconciled state for a pass.
        await stateStore.ClearSuccessAsync(cancellationToken);
        foreach (var step in ordered)
        {
            logger.LogInformation("Running initialization step {Step} with timeout {Timeout}.", step.Name, step.Timeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(step.Timeout);
            try
            {
                await step.ExecuteAsync(deadline.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"Initialization step '{step.Name}' exceeded its {step.Timeout} deadline.", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Initialization step '{step.Name}' failed. Correct the problem and rerun initialization; partial steps are safe to retry.", ex);
            }
        }

        // This is deliberately the final write. A failure in any service leaves no overall-success marker.
        await stateStore.RecordSuccessAsync(CurrentVersion, cancellationToken);
        logger.LogInformation("FrostStream application initialization version {Version} completed.", CurrentVersion);
    }

    internal static IReadOnlyList<IApplicationInitializationStep> OrderSteps(
        IEnumerable<IApplicationInitializationStep> source)
    {
        var remaining = source.ToDictionary(step => step.Name, StringComparer.Ordinal);
        var result = new List<IApplicationInitializationStep>(remaining.Count);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(step => step.Dependencies.All(completed.Contains))
                .OrderBy(step => step.Name, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                var unresolved = string.Join(", ", remaining.Values.Select(step =>
                    $"{step.Name} -> [{string.Join(", ", step.Dependencies)}]"));
                throw new InvalidOperationException($"Initialization steps have missing or cyclic dependencies: {unresolved}");
            }

            foreach (var step in ready)
            {
                remaining.Remove(step.Name);
                completed.Add(step.Name);
                result.Add(step);
            }
        }

        return result;
    }
}
