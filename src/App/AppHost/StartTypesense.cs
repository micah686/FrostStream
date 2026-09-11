namespace AppHost;

// Typesense is a typo-tolerant full-text search engine for the metadata schema.
// Treated as a derived projection of Postgres — the volume can be wiped and rebuilt.
public static class StartTypesense
{
    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> apiKey)
    {

        var deployment = DeploymentRuntime.Current;
        return builder
            .AddContainer(deployment.Names.Typesense, deployment.Images.Typesense.Repository, deployment.Images.Typesense.Tag)
            .WithVolume(deployment.Names.Volume("typesense-data"), Helpers.GetEnv("TYPESENSE_DATA_DIR"))
            .WithEnvironment("TYPESENSE_DATA_DIR", Helpers.GetEnv("TYPESENSE_DATA_DIR"))
            .WithEnvironment("TYPESENSE_API_KEY", apiKey)
            .WithEnvironment("TYPESENSE_ENABLE_CORS", Helpers.GetEnv("TYPESENSE_ENABLE_CORS"))
            // Internal-only: the compose export keeps this off the host network.
            .WithHttpEndpoint(port: Ports.Typesense, targetPort: 8108, name: "http");
    }
}
