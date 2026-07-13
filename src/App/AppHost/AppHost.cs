using AppHost;
using Aspire.Hosting;
using DotNetEnv;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("aspire-docker-demo")
    // WithLocalComposeBuild pins literal image names in the yaml, so the publisher's
    // <SERVICE>_IMAGE placeholders are never referenced — keep them out of .env.
    .ConfigureEnvFile(env =>
    {
        foreach (var key in env.Keys.Where(static k => k.EndsWith("_IMAGE", StringComparison.Ordinal)).ToList())
        {
            env.Remove(key);
        }
    });

// aspire-development.env is the source of truth for all configurable environment
// variables (mode flags, image tags, secrets, tunables). Values in the file override
// variables inherited from the shell.
var devEnvFile = Path.GetFullPath(Path.Combine(builder.AppHostDirectory,  "aspire-development.env"));
if (File.Exists(devEnvFile))
{
    Env.Load(devEnvFile);

    builder.Configuration.AddEnvironmentVariables();
}



var hardening = AppHostHardening.Read(AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("SINGLE_USER_MODE")));
AppHostHardening.Validate(hardening);

var sharedStorageRoot = ResolveStorageRoot(builder);

// Deployment-specific secrets shared by several services. Declared as parameters (not inline
// strings) so the compose publisher emits ${...} references backed by .env instead of baking
// the values into docker-compose.yaml as literals.
var openBaoToken = builder.AddParameter(
    "openbao-token",
    hardening.OpenBaoToken,
    publishValueAsDefault: false,
    secret: true);
var typesenseApiKey = builder.AddParameter(
    "typesense-api-key",
    hardening.TypesenseApiKey,
    publishValueAsDefault: false,
    secret: true);

var nats      = StartNats.Start(builder);
var postgres  = StartPostgres.Start(builder, hardening, sharedStorageRoot);
var openBao   = StartOpenBao.Start(builder, openBaoToken);
var typesense = StartTypesense.Start(builder, typesenseApiKey);
var authentik = StartAuthentik.Start(builder, postgres, hardening);
var openFga   = StartOpenFga.Start(builder, postgres, hardening);
var potProvider = StartPotProvider.Start(builder);

StartServices.Wire(builder, hardening, sharedStorageRoot, nats, postgres, openBao, openBaoToken, typesense, typesenseApiKey, authentik, openFga, potProvider);

builder.Build().Run();

static string ResolveStorageRoot(IDistributedApplicationBuilder builder)
{
    var configured = Environment.GetEnvironmentVariable("FROSTSTREAM_STORAGE_ROOT");
    var root = string.IsNullOrWhiteSpace(configured)
        ? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "data"))
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
