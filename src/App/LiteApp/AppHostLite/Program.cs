using AppHost;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DotNetEnv;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment("aspire-docker-demo")
    // WithLocalComposeBuild pins literal image names in the yaml, so the publisher's
    // <SERVICE>_IMAGE placeholders are never referenced — keep them out of .env.
    .ConfigureEnvFile(env =>
    {
        foreach (var key in env.Keys.Where(static k => k.EndsWith("_IMAGE", StringComparison.Ordinal)).ToList())
        {
            env.Remove(key);
        }
        // Aspire publish deliberately strips parameter values from .env. The profile
        // generator requests a private companion file and installs it after publication.
        if (Environment.GetEnvironmentVariable("FROSTSTREAM_PROFILE_ENV_OUTPUT") is { Length: > 0 } output)
        {
            var lines = new List<string>();
            foreach (var (name, variable) in env)
            {
                var value = variable.Source is ParameterResource parameter
                    ? parameter.GetValueAsync(CancellationToken.None).GetAwaiter().GetResult()
                    : variable.DefaultValue;
                // Compose single quotes preserve literal dollars and spaces in secrets.
                lines.Add(name + "='" + (value ?? "").Replace("'", "\\'") + "'");
            }
            File.WriteAllLines(output, lines);
        }
    });


// aspire-development.env is the source of truth for all configurable environment
// variables (mode flags, image tags, secrets, tunables). Values in the file override
// variables inherited from the shell.
var devEnvFile = Path.GetFullPath(
    Environment.GetEnvironmentVariable("FROSTSTREAM_ENV_FILE") ??
    Path.Combine(builder.AppHostDirectory, "..", "..", "SharedApp", "AppHostCommon", "aspire-development.env"));
if (File.Exists(devEnvFile))
{
    Env.Load(devEnvFile);
}

// Empty optional path values mean "use the deployment default". Remove them before Aspire
// snapshots environment variables into configuration, otherwise parameter publication emits
// an empty bind source instead of the Compose-safe default.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT")))
{
    Environment.SetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT", null);
}

builder.Configuration.AddEnvironmentVariables();
compose.WithDashboard(Helpers.DevelopmentToolsEnabled);

var hardening = AppHostHardening.Read(singleUserMode: true);
AppHostHardening.Validate(hardening, lite: true);
var sharedStorageRoot = ResolveStorageRoot(builder);
var typesenseApiKey = builder.AddParameter("typesense-api-key", hardening.TypesenseApiKey,
    publishValueAsDefault: false, secret: true);
var typesense = StartTypesense.Start(builder, typesenseApiKey);
var potProvider = StartPotProvider.Start(builder);
var clickHouse = StartClickHouse.Start(builder);

StartLite.Start(builder, sharedStorageRoot, typesense, typesenseApiKey, potProvider, clickHouse);

builder.Build().Run();

static string ResolveStorageRoot(IDistributedApplicationBuilder builder)
{
    var configured = Environment.GetEnvironmentVariable("FROSTSTREAM_STORAGE_ROOT");
    var root = string.IsNullOrWhiteSpace(configured)
        ? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "..", "data"))
        : configured;

    if (!Path.IsPathRooted(root))
    {
        throw new InvalidOperationException(
            $"FROSTSTREAM_STORAGE_ROOT must be an absolute path, but was '{root}'.");
    }

    root = Path.GetFullPath(root);
    Directory.CreateDirectory(root);
    return root;
}
