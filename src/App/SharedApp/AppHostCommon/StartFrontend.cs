namespace AppHost;

public static class StartFrontend
{
    public static void Wire(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> backend,
        string backendEndpointName)
    {
        var frontendPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "SharedApp", "Frontend"));
        var frontend = builder.AddViteApp("frontend", frontendPath)
            .WithPnpm()
            .WithExternalHttpEndpoints()
            .WithReference(backend)
            .WaitFor(backend)
            .WithEnvironment("WEBAPI_UPSTREAM", backend.GetEndpoint(backendEndpointName))
            .WithLocalComposeBuild("localhost/froststream-frontend:latest", "App/SharedApp/Frontend/Dockerfile");

        frontend.WithEndpoint("http", endpoint => endpoint.Port = Ports.Frontend, createIfNotExists: false);
    }
}
