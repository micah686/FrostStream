namespace AppHost;

public static class StartFrontend
{
    public static void Wire(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> backend,
        string backendEndpointName, bool loopbackOnly = false)
    {
        var frontendPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "Frontend"));
        var frontend = builder.AddViteApp("frontend", frontendPath)
            .WithPnpm()
            .WithExternalHttpEndpoints()
            .WithReference(backend)
            .WaitFor(backend)
            .WithEnvironment("WEBAPI_UPSTREAM", backend.GetEndpoint(backendEndpointName))
            .WithLocalComposeBuild("localhost/froststream-frontend:latest", "Frontend/Dockerfile");

        if (loopbackOnly)
            frontend.PublishAsDockerComposeService((_, service) =>
            {
                for (var i = 0; i < service.Ports.Count; i++)
                    service.Ports[i] = "127.0.0.1:" + service.Ports[i];
            });

        frontend.WithEndpoint("http", endpoint => endpoint.Port = Ports.Frontend, createIfNotExists: false);
    }
}
