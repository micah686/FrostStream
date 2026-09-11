namespace FrostStream.Deployment;

public enum DeploymentEdition
{
    Full,
    Lite
}

public sealed record DeploymentProfile(
    string Name,
    DeploymentEdition Edition,
    bool IncludeInitialization,
    string InstallationName);

public sealed record DeploymentSelection(
    DeploymentProfile Profile,
    bool IncludeDeveloperTools);
