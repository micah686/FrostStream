using Microsoft.Extensions.Configuration;

namespace AppHost;

/// <summary>Infrastructure composition only; Lite runs the same application modules and frontend.</summary>
public static class StartLite
{
    public static void Start(
        IDistributedApplicationBuilder builder,
        string storageRoot,
        IResourceBuilder<ContainerResource> potProvider,
        ClickHouseResources clickHouse)
    {
        if (builder.Configuration["Persistence:Sqlite:BackupPath"] is not null)
            throw new InvalidOperationException("Persistence:Sqlite:BackupPath is obsolete. Configure Backup:Directory.");
        var publish = builder.ExecutionContext.IsPublishMode;
        var root = publish ? "/data" : storageRoot;
        var databasePath = builder.Configuration["Persistence:Sqlite:Path"] ?? Path.Combine(root, "core.sqlite");
        var database = builder.AddParameter("lite-database-path", databasePath,
            publishValueAsDefault: true);
        var backups = builder.AddParameter("lite-backup-directory",
            builder.Configuration["Backup:Directory"] ?? Path.Combine(root, "backups"),
            publishValueAsDefault: true);
        var keys = builder.AddParameter("lite-keys-path",
            builder.Configuration["Secrets:Local:KeyRingPath"] ?? Path.Combine(root, "keys"),
            publishValueAsDefault: true);
        var lite = builder.AddProject<Projects.Lite>("lite", launchProfileName: null)
            .WithEnvironment("Search__Lucene__Path", publish ? "/search" : storageRoot + "-search")
            .WithEnvironment("Deployment__Mode", "Lite")
            .WithEnvironment("DOTNET_GCHeapHardLimitPercent", "40")
            .WithEnvironment("Persistence__Sqlite__Path", database)
            .WithEnvironment("Backup__Directory", backups)
            .WithEnvironment("Secrets__Local__KeyRingPath", keys)
            .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", root)
            .WithEnvironment("Worker__IncomingRoot", Path.Combine(root, "incoming"))
            .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:8080")
            .WithHttpEndpoint(port: Ports.WebApiHttp, targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithEnvironment("PotBroker__Enabled", "true")
            .WithEnvironment("PotBroker__ProviderUrl", potProvider.GetEndpoint("http"))
            .WithEnvironment("PotProvider__Enabled", "true")
            .WithEnvironment("LiveChat__Enabled", clickHouse.Server is null ? "false" : "true")
            .WaitFor(potProvider)
            .PublishAsDockerFile(c => c
                .WithDockerfile(Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")), "Lite/Backend/Dockerfile")
                .WithImage("localhost/froststream-lite", "latest")
                .WithVolume("froststream-lite-data", "/data")
                .WithVolume("froststream-lite-search", "/search"))
            .WithLocalComposeBuild("localhost/froststream-lite:latest", "Lite/Backend/Dockerfile")
            .WithPackagedLimits("4g", "4");
        lite.PublishAsDockerComposeService((_, service) => service.Healthcheck = new()
        {
            Test = ["CMD", "curl", "-fsS", "http://localhost:8080/health"],
            Interval = "10s", Timeout = "5s", Retries = 12, StartPeriod = "120s"
        });

        if (clickHouse is { Server: { } server, HttpEndpoint: { } endpoint, Password: { } password })
        {
            lite.WithEnvironment("LiveChat__Url", endpoint)
                .WithEnvironment("LiveChat__Database", StartClickHouse.Database)
                .WithEnvironment("LiveChat__User", StartClickHouse.User)
                .WithEnvironment("LiveChat__Password", password)
                .WaitFor(server);
        }
        StartFrontend.Wire(builder, lite, "http", loopbackOnly: !AppHostHardening.IsTruthy(Environment.GetEnvironmentVariable("LITE_TRUSTED_LAN")));
    }
}
