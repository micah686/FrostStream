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
        var server = builder
            .AddContainer(deployment.Names.Typesense, deployment.Images.Typesense.Repository, deployment.Images.Typesense.Tag)
            .WithVolume(deployment.Names.Volume("typesense-data"), Helpers.GetEnv("TYPESENSE_DATA_DIR"))
            .WithEnvironment("TYPESENSE_DATA_DIR", Helpers.GetEnv("TYPESENSE_DATA_DIR"))
            .WithEnvironment("TYPESENSE_API_KEY", apiKey)
            .WithEnvironment("TYPESENSE_ENABLE_CORS", Helpers.GetEnv("TYPESENSE_ENABLE_CORS"))
            // Internal-only: the compose export keeps this off the host network.
            .WithHttpEndpoint(port: Ports.Typesense, targetPort: 8108, name: "http")
            .PublishAsDockerComposeService((_, service) =>
            {
                // The Typesense image has bash but no curl/wget. Probe its real HTTP readiness
                // endpoint over bash's /dev/tcp support instead of testing process existence.
                service.Healthcheck = new()
                {
                    Test =
                    [
                        "CMD",
                        "/usr/bin/bash",
                        "-c",
                        "exec 3<>/dev/tcp/127.0.0.1/8108 && printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && grep -Eq '\"ok\"[[:space:]]*:[[:space:]]*true' <&3"
                    ],
                    Interval = "5s",
                    Timeout = "5s",
                    Retries = 24,
                    StartPeriod = "10s"
                };
                service.Restart = "unless-stopped";
            });

        return server;
    }
}
