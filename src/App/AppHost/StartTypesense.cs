namespace AppHost;

// Typesense is a typo-tolerant full-text search engine for the metadata schema.
// Treated as a derived projection of Postgres — the volume can be wiped and rebuilt.
public static class StartTypesense
{
    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder)
    {

        return builder
            .AddContainer("typesense", "typesense/typesense", "30.2")
            .WithVolume("typesense-data", Helpers.GetEnv("TYPESENSE_DATA_DIR"))
            .WithEnvironment("TYPESENSE_DATA_DIR", Helpers.GetEnv("TYPESENSE_DATA_DIR"))
            .WithEnvironment("TYPESENSE_API_KEY", Helpers.GetEnv("TYPESENSE_API_KEY"))
            .WithEnvironment("TYPESENSE_ENABLE_CORS", Helpers.GetEnv("TYPESENSE_ENABLE_CORS"))
            .WithHttpEndpoint(port: 8108, targetPort: 8108, name: "http");
    }
}
