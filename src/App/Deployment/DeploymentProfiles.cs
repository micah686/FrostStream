namespace FrostStream.Deployment;

public static class DeploymentProfiles
{
    public const string ProfileEnvironmentVariable = "FROSTSTREAM_DEPLOYMENT_PROFILE";
    public const string DeveloperToolsEnvironmentVariable = "FROSTSTREAM_DEV_TOOLS";

    public static readonly DeploymentProfile FullInit = new(
        "frostream-full-init",
        DeploymentEdition.Full,
        IncludeInitialization: true,
        InstallationName: "froststream-full");

    public static readonly DeploymentProfile Full = new(
        "froststream-full",
        DeploymentEdition.Full,
        IncludeInitialization: false,
        InstallationName: "froststream-full");

    public static readonly DeploymentProfile LiteInit = new(
        "frostream-lite-init",
        DeploymentEdition.Lite,
        IncludeInitialization: true,
        InstallationName: "froststream-lite");

    public static readonly DeploymentProfile Lite = new(
        "frostream-lite",
        DeploymentEdition.Lite,
        IncludeInitialization: false,
        InstallationName: "froststream-lite");

    public static IReadOnlyList<DeploymentProfile> All { get; } =
        Array.AsReadOnly<DeploymentProfile>([FullInit, Full, LiteInit, Lite]);

    public static DeploymentSelection Resolve(string[] args)
    {
        var requestedName = ReadOption(args, "--deployment-profile")
            ?? Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(requestedName))
        {
            throw new InvalidOperationException(
                $"A deployment profile is required. Set {ProfileEnvironmentVariable} or pass " +
                $"--deployment-profile <name>. Valid profiles: {string.Join(", ", All.Select(x => x.Name))}.");
        }

        var profile = All.SingleOrDefault(x =>
            string.Equals(x.Name, requestedName, StringComparison.Ordinal));
        if (profile is null)
        {
            throw new InvalidOperationException(
                $"Unknown deployment profile '{requestedName}'. Valid profiles: " +
                $"{string.Join(", ", All.Select(x => x.Name))}.");
        }

        var developerToolsText = ReadOption(args, "--developer-tools")
            ?? Environment.GetEnvironmentVariable(DeveloperToolsEnvironmentVariable);
        var includeDeveloperTools = developerToolsText is not null && ParseBoolean(
            developerToolsText,
            "--developer-tools");

        return new DeploymentSelection(profile, includeDeveloperTools);
    }

    private static string? ReadOption(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith(option + "=", StringComparison.Ordinal))
            {
                return argument[(option.Length + 1)..];
            }

            if (!string.Equals(argument, option, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Option {option} requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }

    private static bool ParseBoolean(string value, string option) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => throw new InvalidOperationException(
            $"Option {option} must be true or false, but was '{value}'.")
    };
}
