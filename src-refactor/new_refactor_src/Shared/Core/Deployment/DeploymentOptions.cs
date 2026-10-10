using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Shared.Deployment;

public enum DeploymentMode { Full, Lite }

/// <summary>Resolved once at startup. Changing deployment mode requires a restart.</summary>
public sealed record DeploymentOptions(DeploymentMode Mode)
{
    public static DeploymentOptions FromConfiguration(IConfiguration configuration)
    {
        var value = configuration["Deployment:Mode"] ?? nameof(DeploymentMode.Full);
        if (!Enum.TryParse<DeploymentMode>(value, true, out var mode) || !Enum.IsDefined(mode) ||
            !Enum.GetNames<DeploymentMode>().Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported Deployment:Mode '{value}'. Expected Full or Lite.");
        return new(mode);
    }
}

public static class DeploymentRegistration
{
    public static IHostApplicationBuilder AddDeployment(this IHostApplicationBuilder builder)
    {
        var deployment = DeploymentOptions.FromConfiguration(builder.Configuration);
        builder.Services.TryAddSingleton(deployment);
        builder.Services.TryAddSingleton(SystemCapabilities.Resolve(deployment, builder.Configuration));
        return builder;
    }
}
