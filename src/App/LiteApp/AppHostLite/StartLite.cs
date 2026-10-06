using Microsoft.Extensions.Configuration;

namespace AppHost;

/// <summary>Infrastructure composition only; Lite runs the same application modules and frontend.</summary>
public static class StartLite
{
    public static void Start(
        IDistributedApplicationBuilder builder,
        string storageRoot,
        IResourceBuilder<ContainerResource> typesense,
        IResourceBuilder<ParameterResource> typesenseApiKey,
        IResourceBuilder<ContainerResource> potProvider,
        ClickHouseResources clickHouse)
    {
        var publish = builder.ExecutionContext.IsPublishMode;
        var root = publish ? "/data" : storageRoot;
        var databasePath = builder.Configuration["Persistence:Sqlite:Path"] ?? Path.Combine(root, "frostreamlitedb");
        var database = builder.AddParameter("lite-database-path", databasePath,
            publishValueAsDefault: true);
        var backups = builder.AddParameter("lite-backup-directory",
            builder.Configuration["Backup:Directory"] ?? builder.Configuration["Persistence:Sqlite:BackupPath"] ?? Path.Combine(root, "backups"),
            publishValueAsDefault: true);
        var keys = builder.AddParameter("lite-keys-path",
            builder.Configuration["Secrets:Local:KeyRingPath"] ?? databasePath + ".keys",
            publishValueAsDefault: true);
        var lite = builder.AddProject<Projects.Lite>("lite", launchProfileName: null)
            .WithEnvironment("Deployment__Mode", "Lite")
            .WithEnvironment("Persistence__Sqlite__Enabled", "true")
            .WithEnvironment("Persistence__Sqlite__Path", database)
            .WithEnvironment("Backup__Directory", backups)
            .WithEnvironment("Secrets__Local__KeyRingPath", keys)
            .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", root)
            .WithEnvironment("Worker__IncomingRoot", Path.Combine(root, "incoming"))
            .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:8080")
            .WithHttpEndpoint(port: Ports.WebApiHttp, targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithEnvironment("Typesense__Url", typesense.GetEndpoint("http"))
            .WithEnvironment("Typesense__ApiKey", typesenseApiKey)
            .WithEnvironment("PotBroker__Enabled", "true")
            .WithEnvironment("PotBroker__ProviderUrl", potProvider.GetEndpoint("http"))
            .WithEnvironment("PotProvider__Enabled", "true")
            .WithEnvironment("LiveChat__Enabled", clickHouse.Server is null ? "false" : "true")
            .WaitFor(typesense).WaitFor(potProvider)
            .PublishAsDockerFile(c => c
                .WithDockerfile(Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "Lite")), "Dockerfile")
                .WithImage("localhost/froststream-lite", "latest")
                .WithVolume("froststream-lite-data", "/data"))
            .WithLocalComposeBuild("localhost/froststream-lite:latest", "App/LiteApp/Lite/Dockerfile");

        if (clickHouse is { Server: { } server, HttpEndpoint: { } endpoint, Password: { } password })
        {
            lite.WithEnvironment("LiveChat__Url", endpoint)
                .WithEnvironment("LiveChat__Database", StartClickHouse.Database)
                .WithEnvironment("LiveChat__User", StartClickHouse.User)
                .WithEnvironment("LiveChat__Password", password)
                .WaitFor(server);
        }
        StartFrontend.Wire(builder, lite, "http");
    }
}
