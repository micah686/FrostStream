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
        if (hardening.SingleUserMode)
        {
            return new OpenFgaResources(Server: null, Endpoint: null);
        }

        var migrate = builder
            .AddContainer("openfga-migrate", "openfga/openfga", hardening.OpenFgaImageTag)
            .WithArgs("migrate")
            .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
            .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgres.User}:{postgres.Password}@postgres:5432/openfgadb?sslmode=disable")
            .WaitFor(postgres.OpenFgaDb);

        var server = builder
            .AddContainer("openfga", "openfga/openfga", hardening.OpenFgaImageTag)
            .WithArgs("run")
            .WithHttpEndpoint(port: 8081, targetPort: 8080, name: "http")
            .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
            .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgres.User}:{postgres.Password}@postgres:5432/openfgadb?sslmode=disable")
            .WithEnvironment("OPENFGA_PLAYGROUND_ENABLED", hardening.EnableFgaAuthenticatedEndpoints ? "false" : "true")
            .WaitForCompletion(migrate)
            .WaitFor(postgres.OpenFgaDb);
        
        var studio = builder
            .AddContainer("openfga-studio", "ghcr.io/prakashm88/openfga-studio", Environment.GetEnvironmentVariable("OPENFGA_STUDIO_IMAGE_TAG") ?? "latest")
            .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "http")
            // Tell Studio not to run its own embedded OpenFGA.
            .WithEnvironment("DISABLE_LOCAL_OPENFGA", "true")
            // Internal container-to-container URL.
            // Use targetPort 8080, not the host-mapped Aspire port 8081.
            .WithEnvironment("OPENFGA_ENDPOINT", "http://openfga:8080")

            .WaitFor(server);

        if (hardening.EnableFgaAuthenticatedEndpoints)
        {
            server = server
                .WithEnvironment("OPENFGA_AUTHN_METHOD", "preshared")
                .WithEnvironment("OPENFGA_AUTHN_PRESHARED_KEYS", hardening.OpenFgaApiToken);
        }

        return new OpenFgaResources(server, server.GetEndpoint("http"));
    }
}
