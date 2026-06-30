using AppHost;
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);


//Test with single-user-mode for easy dev
Environment.SetEnvironmentVariable("SINGLE_USER_MODE", "true");



var hardening = AppHostHardening.Read(AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("SINGLE_USER_MODE")));
AppHostHardening.Validate(hardening);

var sharedStorageRoot = ResolveStorageRoot(builder);

var nats      = StartNats.Start(builder);
var postgres  = StartPostgres.Start(builder, hardening);
var openBao   = StartOpenBao.Start(builder, hardening);
var typesense = StartTypesense.Start(builder, hardening);
var authentik = StartAuthentik.Start(builder, postgres, hardening);
var openFga   = StartOpenFga.Start(builder, postgres, hardening);
var potProvider = StartPotProvider.Start(builder, hardening);

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
