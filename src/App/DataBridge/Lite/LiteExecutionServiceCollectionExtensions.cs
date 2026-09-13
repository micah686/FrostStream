using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Application;

namespace DataBridge.Lite;

public static class LiteExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddLiteDurableExecution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<LiteExecutionOptions>()
            .Bind(configuration.GetSection(LiteExecutionOptions.SectionName))
            .Validate(options => options.MaximumConcurrency > 0, "MaximumConcurrency must be positive.")
            .Validate(options => options.DownloadConcurrency > 0, "DownloadConcurrency must be positive.")
            .Validate(options => options.DownloadConcurrency <= options.MaximumConcurrency,
                "DownloadConcurrency cannot exceed MaximumConcurrency.")
            .Validate(options => options.WakeCapacity is > 0 and <= 4096, "WakeCapacity must be between 1 and 4096.")
            .Validate(options => options.PollInterval > TimeSpan.Zero, "PollInterval must be positive.")
            .Validate(options => options.OwnershipProbeInterval > TimeSpan.Zero,
                "OwnershipProbeInterval must be positive.")
            .ValidateOnStart();

        services.AddDurableWorkflowAbstractions();
        services.AddSingleton<LiteExecutionLease>();
        services.AddSingleton<ILiteExecutionLease>(provider => provider.GetRequiredService<LiteExecutionLease>());
        services.AddHostedService(provider => provider.GetRequiredService<LiteExecutionLease>());
        services.AddSingleton<NpgsqlLocalExecutionStore>();
        services.AddSingleton<ILocalExecutionStore>(provider => provider.GetRequiredService<NpgsqlLocalExecutionStore>());
        services.AddSingleton<IPersistedProgressSnapshotStore<LocalExecutionSnapshot>>(
            provider => provider.GetRequiredService<NpgsqlLocalExecutionStore>());
        services.AddSingleton<LocalExecutionWakeSignal>();
        services.AddSingleton<ILocalExecutionWakeSignal>(provider => provider.GetRequiredService<LocalExecutionWakeSignal>());
        services.AddSingleton<LocalExecutionDispatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<LocalExecutionDispatcher>());
        return services;
    }
}
