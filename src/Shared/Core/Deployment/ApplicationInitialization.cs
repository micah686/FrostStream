using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace Shared.Deployment;
public sealed record ApplicationInitializationStep(string Name, int Order, Func<IServiceProvider, CancellationToken, Task> Run);
public sealed class ApplicationInitialization(IServiceProvider services)
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private bool initialized;
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try
        {
            if (initialized) return;
            foreach (var step in services.GetServices<ApplicationInitializationStep>().OrderBy(s => s.Order))
            {
                ct.ThrowIfCancellationRequested();
                await step.Run(services, ct);
            }
            ct.ThrowIfCancellationRequested();
            initialized = true;
        }
        finally { mutex.Release(); }
    }
}
public static class ApplicationInitializationExtensions
{
    public static Task InitializeApplicationAsync(this IHost host, CancellationToken ct = default)
        => host.Services.GetRequiredService<ApplicationInitialization>().InitializeAsync(ct);
}
public interface IApplicationReadyStep { Task StartAsync(CancellationToken ct); }
