using AppHost;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var deployment = DeploymentBootstrap.Resolve(builder, args);
DeploymentRuntime.Initialize(deployment);

var compose = builder.AddDockerComposeEnvironment(deployment.Selection.Profile.Name)
    .WithDashboard(false)
    .ConfigureComposeFile(file => file.Name = deployment.Names.InstallationName)
    .ConfigureEnvFile(env =>
    {
        foreach (var key in env.Keys.Where(static k => k.EndsWith("_IMAGE", StringComparison.Ordinal)).ToList())
        {
            env.Remove(key);
        }
    });
compose.Resource.DefaultNetworkName = deployment.Names.Network;

builder.Configuration.AddEnvironmentVariables();

var hardening = AppHostHardening.Read(AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("SINGLE_USER_MODE")));
AppHostHardening.Validate(hardening, deployment.Selection.Profile.Edition);

var sharedStorageRoot = deployment.Paths.StorageRoot;
Directory.CreateDirectory(sharedStorageRoot);

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
var openBaoResources = StartOpenBao.Start(builder, sharedStorageRoot, openBaoToken);
var openBao   = openBaoResources.Server;
var typesense = StartTypesense.Start(builder, typesenseApiKey);
var authentik = StartAuthentik.Start(builder, postgres, hardening);
var openFga   = StartOpenFga.Start(builder, postgres, hardening);
var potProvider = StartPotProvider.Start(builder);
var backupService = StartBackupService.Start(builder, sharedStorageRoot, nats, postgres, openBaoResources, openBaoToken);
var clickHouse = StartClickHouse.Start(builder);

StartServices.Wire(builder, hardening, sharedStorageRoot, nats, postgres, openBaoResources, openBaoToken, typesense, typesenseApiKey, authentik, openFga, potProvider, backupService, clickHouse);

builder.Build().Run();
