namespace AppHost;

public static partial class Helpers
{
    internal static string GetEnv(string variable)
        => Environment.GetEnvironmentVariable(variable) ?? "VALUE_NOT_SET";

    internal static bool DevelopmentToolsEnabled
        => AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("FROSTSTREAM_DEV_TOOLS"));

    public static bool IsSingleUserMode
        => AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("SINGLE_USER_MODE"));

    internal static bool LiveChatEnabled
        => AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("LIVE_CHAT_ENABLED"));

    internal static IResourceBuilder<TResource> WithLocalComposeBuild<TResource>(
        this IResourceBuilder<TResource> resource, string image, string dockerfile)
        where TResource : IComputeResource
        => resource.PublishAsDockerComposeService((_, service) =>
        {
            service.Image = image;
            service.PullPolicy = "build";
            service.Build = new Aspire.Hosting.Docker.Resources.ServiceNodes.Build
            {
                Context = "../../..",
                Dockerfile = dockerfile
            };
        });
}
