namespace AppHost;

public static partial class Helpers
{
    internal static string GetEnv(string variable)
        => Environment.GetEnvironmentVariable(variable) ?? "VALUE_NOT_SET";

    internal static bool DevelopmentToolsEnabled
        => AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("FROSTSTREAM_DEV_TOOLS"));

    public static void RejectObsoleteFullIdentityConfiguration()
    {
        foreach (var key in new[] { "SINGLE_USER_MODE", "Auth__SingleUserMode", "AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION", "Auth__AllowSingleUserModeInProduction" })
            if (Environment.GetEnvironmentVariable(key) is not null)
                throw new InvalidOperationException($"{key} is obsolete and forbidden in Full. Full requires Authentik and OpenFGA.");
    }

    internal static bool LiveChatEnabled
        => AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("LIVE_CHAT_ENABLED"));

    /// <summary>
    /// Upgrades the published compose <c>depends_on</c> condition for <paramref name="dependency"/>
    /// (default emitted by WaitFor is <c>service_started</c>). No effect in run mode.
    /// </summary>
    internal static IResourceBuilder<T> WithComposeDependencyCondition<T>(
        this IResourceBuilder<T> resource,
        string dependency,
        string condition)
        where T : IComputeResource
    {
        return resource.PublishAsDockerComposeService((_, service) =>
        {
            if (service.DependsOn.TryGetValue(dependency, out var dep))
            {
                dep.Condition = condition;
            }
        });
    }

    internal static IResourceBuilder<TResource> WithPackagedLimits<TResource>(
        this IResourceBuilder<TResource> resource, string memory, string cpus)
        where TResource : IComputeResource
        => resource.PublishAsDockerComposeService((_, service) =>
        {
            service.Restart = "unless-stopped";
            service.Deploy = new()
            {
                Resources = new()
                {
                    Limits = new() { Memory = memory, Cpus = cpus }
                }
            };
        });

    internal static IResourceBuilder<TResource> WithLocalComposeBuild<TResource>(
        this IResourceBuilder<TResource> resource, string image, string dockerfile)
        where TResource : IComputeResource
        => resource.PublishAsDockerComposeService((_, service) =>
        {
            service.Image = image;
            service.PullPolicy = "build";
            service.Restart = "unless-stopped";
            service.Build = new Aspire.Hosting.Docker.Resources.ServiceNodes.Build
            {
                Context = "../..",
                Dockerfile = dockerfile
            };
        });
}
