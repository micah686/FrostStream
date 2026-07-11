using AppHost;
using Aspire.Hosting;
using DotNetEnv;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("aspire-docker-demo");

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

var nats      = StartNats.Start(builder);
var postgres  = StartPostgres.Start(builder, hardening, sharedStorageRoot);
var openBao   = StartOpenBao.Start(builder);
var typesense = StartTypesense.Start(builder);
var authentik = StartAuthentik.Start(builder, postgres, hardening);
var openFga   = StartOpenFga.Start(builder, postgres, hardening);
var potProvider = StartPotProvider.Start(builder);

StartServices.Wire(builder, hardening, sharedStorageRoot, nats, postgres, openBao, typesense, authentik, openFga, potProvider);

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
