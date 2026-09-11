namespace AppHost;

public sealed record OpenFgaResources(
    IResourceBuilder<ContainerResource>? Server,
    EndpointReference? Endpoint);

public static class StartOpenFga
{
    public static OpenFgaResources Start(
        IDistributedApplicationBuilder builder,
        PostgresResources postgres,
        AppHostHardeningOptions hardening)
    {
        var deployment = DeploymentRuntime.Current;
        if (Helpers.IsSingleUserMode)
        {
            return new OpenFgaResources(Server: null, Endpoint: null);
        }

        var migrate = builder
            .AddContainer(deployment.Names.OpenFgaMigrate, deployment.Images.OpenFga.Repository, deployment.Images.OpenFga.Tag)
            .WithArgs("migrate")
            .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
            .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgres.User}:{postgres.Password}@{deployment.Names.Postgres}:5432/{deployment.Names.OpenFgaDatabase}?sslmode=disable")
            .WaitFor(postgres.OpenFgaDb)
            .WaitForDatabases(postgres);

        var server = builder
            .AddContainer(deployment.Names.OpenFga, deployment.Images.OpenFga.Repository, deployment.Images.OpenFga.Tag)
            .WithArgs("run")
            // Internal-only: the compose export keeps this off the host network.
            .WithHttpEndpoint(port: Ports.OpenFga, targetPort: 8080, name: "http")
            .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
            .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgres.User}:{postgres.Password}@{deployment.Names.Postgres}:5432/{deployment.Names.OpenFgaDatabase}?sslmode=disable")
            .WaitForCompletion(migrate)
            .WaitFor(postgres.OpenFgaDb);
        
        if (Helpers.DevelopmentToolsEnabled)
        {
        var studio = builder
            .AddContainer(deployment.Names.OpenFgaStudio, deployment.Images.OpenFgaStudio.Repository, deployment.Images.OpenFgaStudio.Tag)
            .WithHttpEndpoint(port: Ports.OpenFgaStudio, targetPort: 3000, name: "http")
            .WithExternalHttpEndpoints()
            // Tell Studio not to run its own embedded OpenFGA.
            .WithEnvironment("DISABLE_LOCAL_OPENFGA", "true")
            // Internal container-to-container URL.
            // Use targetPort 8080, not the host-mapped Aspire port 8081.
            .WithEnvironment("OPENFGA_ENDPOINT", $"http://{deployment.Names.OpenFga}:8080")

            .WaitFor(server);
        }

        if (hardening.EnableFgaAuthenticatedEndpoints)
        {
            server = server
                .WithEnvironment("OPENFGA_AUTHN_METHOD", "preshared")
                .WithEnvironment("OPENFGA_AUTHN_PRESHARED_KEYS", Helpers.GetEnv("OPENFGA_API_TOKEN"));
        }

        return new OpenFgaResources(server, server.GetEndpoint("http"));
    }
}
