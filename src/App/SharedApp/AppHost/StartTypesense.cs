namespace AppHost;

// Typesense is a typo-tolerant full-text search engine for the metadata schema.
// Treated as a derived projection of Postgres — the volume can be wiped and rebuilt.
public static class StartTypesense
{
    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> apiKey)
    {

        return builder
            .AddContainer("typesense", "typesense/typesense", "30.2")
            .WithVolume("typesense-data", Environment.GetEnvironmentVariable("TYPESENSE_DATA_DIR") ?? "/data")
            .WithEnvironment("TYPESENSE_DATA_DIR", Environment.GetEnvironmentVariable("TYPESENSE_DATA_DIR") ?? "/data")
            .WithEnvironment("TYPESENSE_API_KEY", apiKey)
            .WithEnvironment("TYPESENSE_ENABLE_CORS", Environment.GetEnvironmentVariable("TYPESENSE_ENABLE_CORS") ?? "false")
            // Internal-only: the compose export keeps this off the host network.
            .WithHttpEndpoint(port: Ports.Typesense, targetPort: 8108, name: "http");
    }
}
