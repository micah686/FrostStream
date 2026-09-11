using System.Text.RegularExpressions;
using FrostStream.Deployment;

if (args is ["--profiles"])
{
    VerifyProfiles();
    return;
}

if (args is ["--production-pair", var initRoot, var runtimeRoot, var installationName, var sourceRoot, var resolvedFile])
{
    VerifyProfiles();
    VerifyProductionPair(
        Path.GetFullPath(initRoot),
        Path.GetFullPath(runtimeRoot),
        installationName,
        Path.GetFullPath(sourceRoot),
        Path.GetFullPath(resolvedFile));
    Console.WriteLine("Phase 3 production publishing verification passed.");
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

        var fromSelectedFile = DeploymentProfiles.Resolve(
            [],
            fallbackValues: Values(
                (DeploymentProfiles.ProfileEnvironmentVariable, DeploymentProfiles.Lite.Name),
                (DeploymentProfiles.DeveloperToolsEnvironmentVariable, "true")));
        Assert(ReferenceEquals(fromSelectedFile.Profile, DeploymentProfiles.Lite),
            "Explicitly selected env-file profile was ignored.");
        Assert(fromSelectedFile.IncludeDeveloperTools,
            "Explicitly selected env-file developer-tools option was ignored.");

        ExpectFailure([], "A deployment profile is required");
        ExpectFailure(["--deployment-profile", "not-a-profile"], "Unknown deployment profile");
        ExpectFailure(["--deployment-profile"], "requires a value");
        ExpectFailure(
            ["--deployment-profile", DeploymentProfiles.Full.Name, "--developer-tools", "sometimes"],
            "must be true or false");

        VerifyDeploymentConfiguration();
    }
    finally
    {
        Environment.SetEnvironmentVariable(DeploymentProfiles.ProfileEnvironmentVariable, previousProfile);
        Environment.SetEnvironmentVariable(DeploymentProfiles.DeveloperToolsEnvironmentVariable, previousDeveloperTools);
    }
}

static void VerifyDeploymentConfiguration()
{
    var appHostDirectory = Path.Combine(Path.GetTempPath(), "phase3-repo", "src", "App", "AppHost");
    var process = Values(
        ("PORT_FRONTEND", "31001"),
        ("FROSTSTREAM_INSTALLATION_NAME", "explicit-install"));
    var selected = Values(
        ("PORT_FRONTEND", "31002"),
        ("PORT_AUTHENTIK", "31102"),
        ("POSTGRES_PASSWORD", ""));
    var preserved = Values(
        ("PORT_FRONTEND", "31003"),
        ("PORT_AUTHENTIK", "31103"),
        ("PORT_WEBAPI_HTTP", "31203"),
        ("POSTGRES_PASSWORD", "preserved-password"));

    var precedence = Resolve(
        DeploymentProfiles.FullInit,
        appHostDirectory,
        process,
        selected,
        preserved,
        "/tmp/phase3-explicit-output",
        "generated-secret");
    Assert(precedence.Ports.Frontend == 31001, "Process environment must win precedence.");
    Assert(precedence.Ports.Authentik == 31102, "Selected env file must beat preserved values.");
    Assert(precedence.Ports.WebApiHttp == 31203, "Preserved values must beat profile defaults.");
    Assert(precedence.Ports.Scheduler == 25300, "Profile port default was not applied.");
    Assert(precedence.Names.InstallationName == "explicit-install", "Installation override was ignored.");
    Assert(precedence.Get("POSTGRES_PASSWORD") == "preserved-password", "Preserved secret was regenerated.");

    var stateDirectory = Path.Combine(Path.GetTempPath(), "froststream-phase3-tests", Guid.NewGuid().ToString("N"));
    var stateFile = Path.Combine(stateDirectory, "resolved.env");
    try
    {
        DeploymentEnvironmentFiles.WriteResolved(
            stateFile,
            DeploymentConfigurationResolver.PersistableValues(precedence.Values));
        var restored = DeploymentEnvironmentFiles.Read(stateFile);
        Assert(restored["POSTGRES_PASSWORD"] == "preserved-password",
            "Resolved deployment state did not preserve the existing secret.");
        Assert(!restored.ContainsKey("UNRELATED_PROCESS_SECRET"),
            "Resolved deployment state captured an unrelated process value.");
        Assert(!restored.ContainsKey(DeploymentProfiles.ProfileEnvironmentVariable),
            "Resolved deployment state captured a transient profile name.");
        Assert(!restored.ContainsKey(DeploymentConfigurationResolver.PublishOutputVariable),
            "Resolved deployment state captured a disposable output path.");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert(File.GetUnixFileMode(stateFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "Resolved deployment state must be owner-readable and owner-writable only.");
        }
    }
    finally
    {
        if (Directory.Exists(stateDirectory))
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    var pairValues = Values(
        ("POSTGRES_PASSWORD", "pair-postgres-password"),
        ("OPENBAO_TOKEN", "pair-openbao-token"),
        ("TYPESENSE_API_KEY", "pair-typesense-key"));
    var fullInit = Resolve(
        DeploymentProfiles.FullInit, appHostDirectory, preserved: pairValues,
        generatedSecret: "full-generated");
    var full = Resolve(
        DeploymentProfiles.Full, appHostDirectory, preserved: pairValues,
        generatedSecret: "full-generated");
    Assert(fullInit.Names.InstallationName == full.Names.InstallationName, "Full pair project identity changed.");
    Assert(fullInit.Names.Network == full.Names.Network, "Full pair network identity changed.");
    Assert(fullInit.Names.Volume("postgres-data") == full.Names.Volume("postgres-data"),
        "Full pair volume identity changed.");
    Assert(fullInit.Paths.StorageRoot == full.Paths.StorageRoot, "Full pair storage path changed.");
    Assert(fullInit.Get("POSTGRES_PASSWORD") == full.Get("POSTGRES_PASSWORD"),
        "Full pair credentials changed.");

    var alpha = Resolve(
        DeploymentProfiles.Full, appHostDirectory,
        process: Values(("FROSTSTREAM_INSTALLATION_NAME", "froststream-alpha")),
        generatedSecret: "alpha-secret");
    var beta = Resolve(
        DeploymentProfiles.Full, appHostDirectory,
        process: Values(("FROSTSTREAM_INSTALLATION_NAME", "froststream-beta")),
        generatedSecret: "beta-secret");
    Assert(alpha.Names.Network != beta.Names.Network, "Independent installations share a network name.");
    Assert(alpha.Names.Volume("postgres-data") != beta.Names.Volume("postgres-data"),
        "Independent installations share a database volume name.");
    Assert(alpha.Paths.StorageRoot != beta.Paths.StorageRoot, "Independent installations share storage.");
    Assert(alpha.Paths.ResolvedEnvironmentFile != beta.Paths.ResolvedEnvironmentFile,
        "Independent installations share resolved configuration.");
    Assert(alpha.Get("POSTGRES_PASSWORD") != beta.Get("POSTGRES_PASSWORD"),
        "Independent installations generated the same credential.");

    var defaultPaths = Resolve(DeploymentProfiles.Full, appHostDirectory, generatedSecret: "default-secret");
    var alternateOutput = Path.Combine(Path.GetTempPath(), "phase3-alternate", "nested", "full");
    var alternatePaths = Resolve(
        DeploymentProfiles.Full, appHostDirectory,
        requestedOutput: alternateOutput,
        generatedSecret: "default-secret");
    Assert(defaultPaths.Paths.PublishOutputDirectory.EndsWith(
            "docker-compose-artifacts/froststream-full/froststream-full", StringComparison.Ordinal),
        "Default output path is incorrect.");
    Assert(defaultPaths.Paths.StorageRoot == Path.GetFullPath(Path.Combine(appHostDirectory, "..", "..", "..", "data")),
        "Default Full storage path must preserve the existing repository data root.");
    var litePaths = Resolve(DeploymentProfiles.Lite, appHostDirectory, generatedSecret: "lite-secret");
    Assert(litePaths.Paths.StorageRoot.EndsWith("data/froststream-lite", StringComparison.Ordinal),
        "Default Lite storage path is not edition-isolated.");
    Assert(alternatePaths.Paths.PublishOutputDirectory == Path.GetFullPath(alternateOutput),
        "Alternate output path is incorrect.");
    Assert(alternatePaths.Paths.ComposeBuildContext ==
           Path.GetRelativePath(alternateOutput, alternatePaths.Paths.SourceRoot).Replace('\\', '/'),
        "Alternate build context is incorrect.");

    ExpectConfigurationFailure(
        DeploymentProfiles.Full,
        appHostDirectory,
        Values(("FROSTSTREAM_INSTALLATION_NAME", "INVALID"), ("POSTGRES_PASSWORD", "do-not-leak")),
        "Invalid Full deployment configuration",
        "do-not-leak");
    ExpectConfigurationFailure(
        DeploymentProfiles.Lite,
        appHostDirectory,
        Values(("PORT_FRONTEND", "secret-invalid-port")),
        "Invalid Lite deployment configuration",
        "secret-invalid-port");
}

static DeploymentConfiguration Resolve(
    DeploymentProfile profile,
    string appHostDirectory,
    IReadOnlyDictionary<string, string>? process = null,
    IReadOnlyDictionary<string, string>? selected = null,
    IReadOnlyDictionary<string, string>? preserved = null,
    string? requestedOutput = null,
    string generatedSecret = "generated-secret") =>
    DeploymentConfigurationResolver.Resolve(new(
        new DeploymentSelection(profile, IncludeDeveloperTools: false),
        appHostDirectory,
        IsRunMode: false,
        process ?? Values(),
        selected ?? Values(),
        preserved ?? Values(),
        requestedOutput,
        () => generatedSecret));

static IReadOnlyDictionary<string, string> Values(params (string Name, string Value)[] values) =>
    values.ToDictionary(static item => item.Name, static item => item.Value, StringComparer.Ordinal);

static void ExpectConfigurationFailure(
    DeploymentProfile profile,
    string appHostDirectory,
    IReadOnlyDictionary<string, string> process,
    string expectedMessage,
    string sensitiveValue)
{
    try
    {
        Resolve(profile, appHostDirectory, process: process);
        throw new InvalidOperationException($"Expected {profile.Edition} configuration failure.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains(expectedMessage, StringComparison.Ordinal) &&
        !exception.Message.Contains(sensitiveValue, StringComparison.Ordinal))
    {
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

static void VerifyProductionPair(
    string initRoot,
    string runtimeRoot,
    string installationName,
    string sourceRoot,
    string resolvedFile)
{
    var initCompose = ReadRequired(Path.Combine(initRoot, "docker-compose.yaml"));
    var runtimeCompose = ReadRequired(Path.Combine(runtimeRoot, "docker-compose.yaml"));
    var initEnvironment = ReadRequired(Path.Combine(initRoot, ".env"));
    var runtimeEnvironment = ReadRequired(Path.Combine(runtimeRoot, ".env"));
    var resolved = DeploymentEnvironmentFiles.Read(resolvedFile);

    Assert(initCompose == runtimeCompose,
        "Full pair runtime definitions diverged before the Phase 5 lifecycle split.");
    Assert(initEnvironment == runtimeEnvironment, "Full pair env contracts differ.");
    Assert(Regex.IsMatch(initCompose,
            $"^name:\\s*[\\\"']?{Regex.Escape(installationName)}[\\\"']?\\s*$",
            RegexOptions.Multiline),
        "Full Compose project identity is incorrect.");

    var services = ReadTopLevelKeys(initCompose, "services");
    foreach (var required in new[]
             {
                 "nats", "postgres", "openbao", "typesense", "authentik", "openfga",
                 "pot-provider", "backupservice", "databridge", "webapi", "worker",
                 "mediaprocessor", "scheduler", "frontend"
             })
    {
        Assert(services.Contains(required), $"Full production service {required} is missing.");
    }

    foreach (var developmentOnly in new[] { "aspire-dashboard", "dbgate", "nats-ui", "openfga-studio" })
    {
        Assert(!services.Any(service => service.Contains(developmentOnly, StringComparison.OrdinalIgnoreCase)),
            $"Development service {developmentOnly} leaked into Full output.");
    }

    var volumes = ReadTopLevelKeys(initCompose, "volumes");
    Assert(volumes.Count > 0, "Full output defines no persistent volumes.");
    var names = new DeploymentNames(installationName);
    foreach (var requiredVolume in new[]
             {
                 names.Volume("postgres-data"),
                 names.Volume("postgres-socket"),
                 names.Volume("openbao-data"),
                 names.Volume("typesense-data"),
                 names.Volume("data"),
                 names.Volume("data-protection-keys")
             })
    {
        Assert(volumes.Contains(requiredVolume), $"Full volume {requiredVolume} is missing.");
    }
    Assert(initCompose.Contains($"  {installationName}-network:", StringComparison.Ordinal),
        "Installation network is missing.");

    foreach (Match match in Regex.Matches(initCompose, "^\\s+context:\\s*[\\\"']?([^\\\"'\\r\\n]+)[\\\"']?\\s*$", RegexOptions.Multiline))
    {
        var actualSourceRoot = Path.GetFullPath(Path.Combine(initRoot, match.Groups[1].Value));
        Assert(actualSourceRoot == sourceRoot, $"Build context does not resolve to {sourceRoot}.");
    }

    foreach (var secretName in DeploymentConfigurationResolver.SecretInputNames)
    {
        if (!resolved.TryGetValue(secretName, out var secretValue) || string.IsNullOrWhiteSpace(secretValue))
        {
            continue;
        }

        Assert(!initCompose.Contains(secretValue, StringComparison.Ordinal),
            $"Resolved secret {secretName} leaked into Compose output.");
        Assert(!initEnvironment.Contains(secretValue, StringComparison.Ordinal),
            $"Resolved secret {secretName} leaked into the published env contract.");
    }

    Assert(new FileInfo(resolvedFile).Exists, "Resolved installation file is missing.");
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        Assert(File.GetUnixFileMode(resolvedFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
            "Resolved installation file permissions are not 0600.");
    }
}

static string ReadRequired(string path)
{
    Assert(File.Exists(path), $"Missing {path}.");
    return File.ReadAllText(path);
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
