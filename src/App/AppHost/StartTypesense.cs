namespace AppHost;

// Typesense is a typo-tolerant full-text search engine for the metadata schema.
// Treated as a derived projection of Postgres — the volume can be wiped and rebuilt.
public static class StartTypesense
{
    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening)
    {
        return builder
            .AddContainer("typesense", "typesense/typesense", "30.2")
            .WithVolume("typesense-data", "/data")
            .WithEnvironment("TYPESENSE_DATA_DIR", "/data")
            .WithEnvironment("TYPESENSE_API_KEY", hardening.TypesenseApiKey)
            .WithEnvironment("TYPESENSE_ENABLE_CORS", "true")
            .WithHttpEndpoint(port: 8108, targetPort: 8108, name: "http");
    }
}
