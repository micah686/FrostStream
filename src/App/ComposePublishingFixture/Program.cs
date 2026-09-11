using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FrostStream.Deployment;

var selection = DeploymentProfiles.Resolve(args);
var profile = selection.Profile;
var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment(profile.Name)
    .WithDashboard(false)
    .ConfigureComposeFile(file => file.Name = profile.InstallationName)
    .ConfigureEnvFile(environment =>
    {
        if (environment.TryGetValue("FIXTURE_SHARED_KEY", out var sharedKey))
        {
            sharedKey.Description =
                $"Phase 2 {profile.Edition} fixture value shared by the init/runtime profile pair.";
        }
    });

var sharedKeyDefault = profile.Edition == DeploymentEdition.Full
    ? "full-resolved-fixture-value"
    : "lite-resolved-fixture-value";
var sharedKey = builder.AddParameter(
    "fixture-shared-key",
    sharedKeyDefault,
    publishValueAsDefault: false,
    secret: true);

var dataVolume = $"{profile.InstallationName}-fixture-data";
var database = builder.AddContainer("fixture-database", "alpine", "3.22")
    .WithArgs("sh", "-c", "while true; do sleep 3600; done")
    .WithVolume(dataVolume, "/fixture-data")
    .PublishAsDockerComposeService((_, service) => service.Restart = "unless-stopped");

IResourceBuilder<ContainerResource>? initializer = null;
if (profile.IncludeInitialization)
{
    initializer = builder.AddContainer("fixture-initialize", "alpine", "3.22")
        .WithArgs("sh", "-c", "test -d /fixture-data")
        .WithVolume(dataVolume, "/fixture-data")
        .WaitFor(database)
        .PublishAsDockerComposeService((_, service) => service.Restart = "no");
}

var application = builder.AddContainer("fixture-application", "alpine", "3.22")
    .WithArgs("sh", "-c", "while true; do sleep 3600; done")
    .WithEnvironment("FIXTURE_EDITION", profile.Edition.ToString().ToLowerInvariant())
    .WithEnvironment("FIXTURE_SHARED_KEY", sharedKey)
    .WaitFor(database)
    .PublishAsDockerComposeService((composeService, service) =>
    {
        service.Restart = "unless-stopped";
        service.Environment["FIXTURE_SHARED_KEY"] = sharedKey.AsEnvironmentPlaceholder(composeService);
    });

if (initializer is not null)
{
    application.WaitForCompletion(initializer);
}

builder.AddContainer(
    profile.Edition == DeploymentEdition.Full ? "fixture-full-only" : "fixture-lite-only",
    "alpine",
    "3.22")
    .WithArgs("sh", "-c", "while true; do sleep 3600; done");

if (selection.IncludeDeveloperTools)
{
    builder.AddContainer("fixture-developer-tools", "alpine", "3.22")
        .WithArgs("sh", "-c", "while true; do sleep 3600; done");
}

builder.Build().Run();
