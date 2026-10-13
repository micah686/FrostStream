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
            .WithLocalComposeBuild("localhost/froststream-frontend:latest", "Frontend/Dockerfile")
            .WithPackagedLimits("128m", "1");
        frontend.PublishAsDockerComposeService((_, service) => service.Healthcheck = new()
        {
            Test = ["CMD", "wget", "-qO-", "http://localhost:25000/readyz"],
            Interval = "10s", Timeout = "5s", Retries = 12, StartPeriod = "120s"
        });

        if (loopbackOnly)
            frontend.PublishAsDockerComposeService((_, service) =>
            {
                for (var i = 0; i < service.Ports.Count; i++)
                    service.Ports[i] = "127.0.0.1:" + service.Ports[i];
            });

        if (builder.ExecutionContext.IsPublishMode) frontend.WithEnvironment("PORT", "25000");
        frontend.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = Ports.Frontend;
            if (builder.ExecutionContext.IsPublishMode) endpoint.TargetPort = 25000;
        }, createIfNotExists: false);
    }
}
