using System.Text.RegularExpressions;
using FrostStream.Deployment;

if (args is ["--profiles"])
{
    VerifyProfiles();
    return;
}

if (args.Length is not 2 and not 3)
{
    throw new InvalidOperationException(
        "Usage: ComposePublishingFixture.Tests <published-root> <prepared-root> [comparison-published-root] or --profiles");
}

VerifyProfiles();
var publishedArtifactsRoot = Path.GetFullPath(args[0]);
VerifyPublishedArtifacts(publishedArtifactsRoot);
VerifyPreparedArtifacts(Path.GetFullPath(args[1]));
if (args.Length == 3)
{
    VerifyStablePublication(publishedArtifactsRoot, Path.GetFullPath(args[2]));
}
Console.WriteLine("Phase 2 Compose publishing fixture verification passed.");

static void VerifyProfiles()
{
    var previousProfile = Environment.GetEnvironmentVariable(DeploymentProfiles.ProfileEnvironmentVariable);
    var previousDeveloperTools = Environment.GetEnvironmentVariable(DeploymentProfiles.DeveloperToolsEnvironmentVariable);
    Environment.SetEnvironmentVariable(DeploymentProfiles.ProfileEnvironmentVariable, null);
    Environment.SetEnvironmentVariable(DeploymentProfiles.DeveloperToolsEnvironmentVariable, null);

    try
    {
        var expected = new[]
        {
            ("frostream-full-init", DeploymentEdition.Full, true, "froststream-full"),
            ("froststream-full", DeploymentEdition.Full, false, "froststream-full"),
            ("frostream-lite-init", DeploymentEdition.Lite, true, "froststream-lite"),
            ("frostream-lite", DeploymentEdition.Lite, false, "froststream-lite")
        };

        Assert(DeploymentProfiles.All.Count == expected.Length, "Exactly four profiles must exist.");
        foreach (var item in expected)
        {
            var profile = DeploymentProfiles.All.Single(x => x.Name == item.Item1);
            Assert(profile.Edition == item.Item2, $"Edition mismatch for {profile.Name}.");
            Assert(profile.IncludeInitialization == item.Item3, $"Lifecycle mismatch for {profile.Name}.");
            Assert(profile.InstallationName == item.Item4, $"Installation mismatch for {profile.Name}.");
        }

        var withTools = DeploymentProfiles.Resolve(
            ["--deployment-profile", DeploymentProfiles.Lite.Name, "--developer-tools", "true"]);
        Assert(ReferenceEquals(withTools.Profile, DeploymentProfiles.Lite),
            "Profile selection must reuse the immutable profile definition.");
        Assert(withTools.IncludeDeveloperTools, "Developer tools option was not selected separately.");
        Assert(!DeploymentProfiles.Resolve(
                ["--deployment-profile", DeploymentProfiles.Lite.Name]).IncludeDeveloperTools,
            "Developer tools must default to disabled.");

        Environment.SetEnvironmentVariable(
            DeploymentProfiles.ProfileEnvironmentVariable,
            DeploymentProfiles.Full.Name);
        Assert(ReferenceEquals(DeploymentProfiles.Resolve([]).Profile, DeploymentProfiles.Full),
            "Environment profile selection failed.");
        Environment.SetEnvironmentVariable(DeploymentProfiles.ProfileEnvironmentVariable, null);

        ExpectFailure([], "A deployment profile is required");
        ExpectFailure(["--deployment-profile", "not-a-profile"], "Unknown deployment profile");
        ExpectFailure(["--deployment-profile"], "requires a value");
        ExpectFailure(
            ["--deployment-profile", DeploymentProfiles.Full.Name, "--developer-tools", "sometimes"],
            "must be true or false");
    }
    finally
    {
        Environment.SetEnvironmentVariable(DeploymentProfiles.ProfileEnvironmentVariable, previousProfile);
        Environment.SetEnvironmentVariable(DeploymentProfiles.DeveloperToolsEnvironmentVariable, previousDeveloperTools);
    }
}

static void ExpectFailure(string[] arguments, string expectedMessage)
{
    try
    {
        DeploymentProfiles.Resolve(arguments);
        throw new InvalidOperationException($"Expected profile resolution to fail with '{expectedMessage}'.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
    }
}

static void VerifyPreparedArtifacts(string root)
{
    foreach (var profile in DeploymentProfiles.All)
    {
        var directory = Path.Combine(root, profile.Name);
        var publishedEnvironmentPath = Path.Combine(directory, ".env");
        var preparedEnvironmentPath = Path.Combine(directory, ".env.phase2");
        Assert(File.Exists(publishedEnvironmentPath), $"Missing {publishedEnvironmentPath}.");
        Assert(File.Exists(preparedEnvironmentPath), $"Missing {preparedEnvironmentPath}.");

        var publishedEnvironment = File.ReadAllText(publishedEnvironmentPath);
        var preparedEnvironment = File.ReadAllText(preparedEnvironmentPath);
        var expected = profile.Edition == DeploymentEdition.Full
            ? "full-resolved-fixture-value"
            : "lite-resolved-fixture-value";

        Assert(Regex.IsMatch(publishedEnvironment, "^FIXTURE_SHARED_KEY=\\s*$", RegexOptions.Multiline),
            $"Prepare changed the publication contract for {profile.Name}.");
        Assert(Regex.IsMatch(preparedEnvironment,
                $"^FIXTURE_SHARED_KEY={Regex.Escape(expected)}\\s*$", RegexOptions.Multiline),
            $"Prepared env did not resolve the expected value for {profile.Name}.");
    }
}

static void VerifyStablePublication(string expectedRoot, string actualRoot)
{
    foreach (var profile in DeploymentProfiles.All)
    {
        foreach (var fileName in new[] { "docker-compose.yaml", ".env" })
        {
            var expected = File.ReadAllBytes(Path.Combine(expectedRoot, profile.Name, fileName));
            var actual = File.ReadAllBytes(Path.Combine(actualRoot, profile.Name, fileName));
            Assert(expected.AsSpan().SequenceEqual(actual),
                $"Repeated publication changed {profile.Name}/{fileName}.");
        }
    }
}

static void VerifyPublishedArtifacts(string root)
{
    foreach (var profile in DeploymentProfiles.All)
    {
        var directory = Path.Combine(root, profile.Name);
        var composePath = Path.Combine(directory, "docker-compose.yaml");
        var environmentPath = Path.Combine(directory, ".env");
        Assert(File.Exists(composePath), $"Missing {composePath}.");
        Assert(File.Exists(environmentPath), $"Missing {environmentPath}.");

        var yaml = File.ReadAllText(composePath);
        var environment = File.ReadAllText(environmentPath);
        var services = ReadTopLevelKeys(yaml, "services");
        var volumes = ReadTopLevelKeys(yaml, "volumes");

        Assert(Regex.IsMatch(yaml, $"^name:\\s*[\\\"']?{Regex.Escape(profile.InstallationName)}[\\\"']?\\s*$", RegexOptions.Multiline),
            $"Compose project name mismatch for {profile.Name}.");
        Assert(services.Contains("fixture-database"), $"Database missing from {profile.Name}.");
        Assert(services.Contains("fixture-application"), $"Application missing from {profile.Name}.");
        Assert(!services.Any(x => x.Contains("dashboard", StringComparison.OrdinalIgnoreCase)),
            $"Dashboard leaked into {profile.Name}.");
        Assert(services.Contains(profile.Edition == DeploymentEdition.Full ? "fixture-full-only" : "fixture-lite-only"),
            $"Edition marker missing from {profile.Name}.");
        Assert(!services.Contains(profile.Edition == DeploymentEdition.Full ? "fixture-lite-only" : "fixture-full-only"),
            $"Wrong edition marker leaked into {profile.Name}.");
        Assert(services.Contains("fixture-initialize") == profile.IncludeInitialization,
            $"Initialization membership mismatch for {profile.Name}.");
        Assert(!services.Contains("fixture-developer-tools"), $"Developer tools leaked into {profile.Name}.");
        Assert(volumes.Contains($"{profile.InstallationName}-fixture-data"),
            $"Stable pair volume missing from {profile.Name}.");
        Assert(Regex.IsMatch(yaml, "^\\s+FIXTURE_SHARED_KEY:\\s*[\\\"']?\\$\\{FIXTURE_SHARED_KEY\\}[\\\"']?\\s*$", RegexOptions.Multiline),
            $"Parameter placeholder missing from {profile.Name}.");
        Assert(Regex.IsMatch(environment, "^FIXTURE_SHARED_KEY=\\s*$", RegexOptions.Multiline),
            $"Published env contract must leave FIXTURE_SHARED_KEY empty for {profile.Name}.");

        var application = ReadServiceBlock(yaml, "fixture-application");
        Assert(application.Contains("fixture-database", StringComparison.Ordinal),
            $"Runtime dependency missing from {profile.Name}.");
        Assert(application.Contains("fixture-initialize", StringComparison.Ordinal) == profile.IncludeInitialization,
            $"Conditional init dependency mismatch for {profile.Name}.");
    }
}

static HashSet<string> ReadTopLevelKeys(string yaml, string section)
{
    var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var result = new HashSet<string>(StringComparer.Ordinal);
    var inSection = false;
    foreach (var line in lines)
    {
        if (!inSection)
        {
            inSection = line == section + ":";
            continue;
        }

        if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
        {
            break;
        }

        var match = Regex.Match(line, "^  ([A-Za-z0-9_-]+):$");
        if (match.Success)
        {
            result.Add(match.Groups[1].Value);
        }
    }

    return result;
}

static string ReadServiceBlock(string yaml, string service)
{
    var match = Regex.Match(
        yaml,
        $"(?ms)^  {Regex.Escape(service)}:\\s*$.*?(?=^  [A-Za-z0-9_-]+:\\s*$|^[A-Za-z0-9_-]+:\\s*$|\\z)");
    Assert(match.Success, $"Could not read service block {service}.");
    return match.Value;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
