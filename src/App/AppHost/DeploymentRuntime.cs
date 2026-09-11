using FrostStream.Deployment;

namespace AppHost;

internal static class DeploymentRuntime
{
    private static DeploymentConfiguration? configuration;

    public static DeploymentConfiguration Current => configuration ??
        throw new InvalidOperationException("Deployment configuration has not been initialized.");

    public static void Initialize(DeploymentConfiguration value)
    {
        if (configuration is not null)
        {
            throw new InvalidOperationException("Deployment configuration can only be initialized once.");
        }

        configuration = value;
    }
}
