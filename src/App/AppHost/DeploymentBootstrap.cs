using FrostStream.Deployment;

namespace AppHost;

internal static class DeploymentBootstrap
{
    private const string EnvironmentFileVariable = "FROSTSTREAM_ENV_FILE";

    public static DeploymentConfiguration Resolve(
        IDistributedApplicationBuilder builder,
        string[] args)
    {
        var explicitEnvironmentFile = DeploymentProfiles.ReadOption(args, "--deployment-env")
            ?? Environment.GetEnvironmentVariable(EnvironmentFileVariable);
        var environmentFile = explicitEnvironmentFile;
        if (string.IsNullOrWhiteSpace(environmentFile) && builder.ExecutionContext.IsRunMode)
        {
            environmentFile = Path.Combine(builder.AppHostDirectory, "aspire-development.env");
        }

        if (!string.IsNullOrWhiteSpace(environmentFile))
        {
            environmentFile = Path.GetFullPath(environmentFile, Directory.GetCurrentDirectory());
            if (!File.Exists(environmentFile))
            {
                throw new InvalidOperationException(
                    $"Deployment environment file was not found: {environmentFile}");
            }
        }

        var selectedFileValues = DeploymentEnvironmentFiles.Read(environmentFile);

        var selection = DeploymentProfiles.Resolve(
            args,
            builder.ExecutionContext.IsRunMode ? DeploymentProfiles.FullInit : null,
            selectedFileValues);
        if (selection.Profile.Edition == DeploymentEdition.Lite)
        {
            throw new InvalidOperationException(
                $"The {selection.Profile.Name} production graph is not implemented yet. " +
                "Lite runtime composition begins in Phase 8.");
        }

        var requestedOutputPath = DeploymentProfiles.ReadOption(args, "--deployment-output-path");
        var processValues = DeploymentEnvironmentFiles.SnapshotProcessEnvironment(
            DeploymentConfigurationResolver.InputNames);

        var discoveryConfiguration = DeploymentConfigurationResolver.Resolve(new(
            selection,
            builder.AppHostDirectory,
            builder.ExecutionContext.IsRunMode,
            processValues,
            selectedFileValues,
            PreservedValues: new Dictionary<string, string>(),
            RequestedOutputPath: requestedOutputPath,
            SecretGenerator: static () => "unpersisted-discovery-value"));
        var preservedValues = DeploymentEnvironmentFiles.Read(
            discoveryConfiguration.Paths.ResolvedEnvironmentFile);

        var configuration = DeploymentConfigurationResolver.Resolve(new(
            selection,
            builder.AppHostDirectory,
            builder.ExecutionContext.IsRunMode,
            processValues,
            selectedFileValues,
            preservedValues,
            requestedOutputPath));

        if (builder.ExecutionContext.IsPublishMode)
        {
            DeploymentEnvironmentFiles.WriteResolved(
                configuration.Paths.ResolvedEnvironmentFile,
                DeploymentConfigurationResolver.PersistableValues(configuration.Values));
        }

        DeploymentEnvironmentFiles.ApplyMissing(configuration.Values);
        return configuration;
    }
}
