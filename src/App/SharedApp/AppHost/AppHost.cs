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
    Path.Combine(builder.AppHostDirectory, "aspire-development.env"));
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

var mode = builder.Configuration["Deployment:Mode"] ?? "Full";
if (!string.Equals(mode, "Full", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(mode, "Lite", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Unsupported Deployment:Mode '{mode}'. Expected Full or Lite.");
var lite = string.Equals(mode, "Lite", StringComparison.OrdinalIgnoreCase);
var hardening = AppHostHardening.Read(lite || AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("SINGLE_USER_MODE")));
AppHostHardening.Validate(hardening, lite);
var sharedStorageRoot = ResolveStorageRoot(builder);
var typesenseApiKey = builder.AddParameter("typesense-api-key", hardening.TypesenseApiKey,
    publishValueAsDefault: false, secret: true);
var typesense = StartTypesense.Start(builder, typesenseApiKey);
var potProvider = StartPotProvider.Start(builder);
var clickHouse = StartClickHouse.Start(builder);

if (lite)
{
    StartLite.Start(builder, sharedStorageRoot, typesense, typesenseApiKey, potProvider, clickHouse);
}
else
{
    var openBaoToken = builder.AddParameter("openbao-token", hardening.OpenBaoToken,
        publishValueAsDefault: false, secret: true);
    var nats = StartNats.Start(builder);
    var postgres = StartPostgres.Start(builder, hardening, sharedStorageRoot);
    var openBaoResources = StartOpenBao.Start(builder, sharedStorageRoot, openBaoToken);
    var authentik = StartAuthentik.Start(builder, postgres, hardening);
    var openFga = StartOpenFga.Start(builder, postgres, hardening);
    var backupService = StartBackupService.Start(builder, sharedStorageRoot, nats, postgres, openBaoResources, openBaoToken);
    StartServices.Wire(builder, hardening, sharedStorageRoot, nats, postgres, openBaoResources,
        openBaoToken, typesense, typesenseApiKey, authentik, openFga, potProvider, backupService, clickHouse);
}

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
