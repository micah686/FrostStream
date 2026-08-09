namespace AppHost;

public static class StartNats
{
    public static IResourceBuilder<NatsServerResource> Start(IDistributedApplicationBuilder builder)
    {
        //Generate TLS certs for SSL
        var certsDirectory = Path.Combine(builder.AppHostDirectory, "configs", "nats", "certs");
        var websocketCertPath = Path.Combine(certsDirectory, "ws-cert.pem");
        var websocketKeyPath = Path.Combine(certsDirectory, "ws-key.pem");
        if (!File.Exists(websocketCertPath) || !File.Exists(websocketKeyPath))
        {
            Helpers.EnsureNatsWebSocketTlsCertificates(websocketCertPath, websocketKeyPath);
        }

        var nats = builder
            .AddNats("nats") // logical name "nats"
            //.WithDataVolume("nats-data")    // persist JS data across restarts (uses a Docker volume)
            .WithPortableBindMount("./configs/nats/nats-server.conf", "../AppHost/configs/nats/nats-server.conf", "/etc/nats/nats.conf", isReadOnly: true)
            .WithPortableBindMount(websocketCertPath, "../AppHost/configs/nats/certs/ws-cert.pem", "/etc/nats/certs/ws-cert.pem", isReadOnly: true)
            .WithPortableBindMount(websocketKeyPath, "../AppHost/configs/nats/certs/ws-key.pem", "/etc/nats/certs/ws-key.pem", isReadOnly: true)
            .WithArgs("-c", "/etc/nats/nats.conf")
            // Proxyless endpoints pin the host port and avoid Aspire's inner-loop proxy. On
            // Windows + Docker Desktop the proxy path allocates random ephemeral host ports
            // (32768+) which frequently collide with the Windows NAT / Docker port proxy.
            .WithEndpoint("tcp", endpoint =>
            {
                endpoint.Port = Ports.NatsClient;
                endpoint.TargetPort = 4222;
                endpoint.IsProxied = false;
            }, createIfNotExists: false)
            .WithHttpEndpoint(port: Ports.NatsMonitor, targetPort: 8222, name: "monitor", isProxied: false)
            .WithEndpoint(port: Ports.NatsWebSocket, targetPort: 9222, name: "ws", isProxied: false);

        // The built-in AddNats health check opens a NATS connection from the AppHost process.
        // With proxyless endpoints that connection string resolves to the container-network
        // address on Windows, so the check fails even though the server is ready. Replace it
        // with an HTTP check against the monitoring endpoint.
        nats.Resource.Annotations.Remove(
            nats.Resource.Annotations
                .OfType<Aspire.Hosting.ApplicationModel.HealthCheckAnnotation>()
                .Single(a => a.Key == "nats_check"));
        nats.WithHttpHealthCheck("/healthz", endpointName: "monitor");

#if DEBUG
        AddNatsUI(builder, nats);
#endif
        
        return nats; //return the nats instance for for others to use as reference
    }

    private static void AddNatsUI(IDistributedApplicationBuilder builder, IResourceBuilder<NatsServerResource> nats)
    {
        // Parameters (not inline strings) so the compose publisher writes them to .env
        // instead of baking the credentials into docker-compose.yaml.
        var adminUser = builder.AddParameter(
            "nats-ui-admin-user",
            Helpers.GetEnv("NATS_UI_ADMIN_USER"),
            publishValueAsDefault: false);
        var adminPass = builder.AddParameter(
            "nats-ui-admin-pass",
            Helpers.GetEnv("NATS_UI_ADMIN_PASS"),
            publishValueAsDefault: false,
            secret: true);
        var jwtSecret = builder.AddParameter(
            "nats-ui-jwt-secret",
            Environment.GetEnvironmentVariable("NATS_UI_JWT_SECRET") ?? Guid.NewGuid().ToString(),
            publishValueAsDefault: false,
            secret: true);

        var natsUi = builder
            .AddContainer("nats-ui", "klinux/nats-ui", "0.4.0")
            .WithHttpEndpoint(port: Ports.NatsUi, targetPort: 8080, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("PORT", "8080")
            .WithEnvironment("BASE_URL", "http://localhost:" + Ports.NatsUi)
            .WithEnvironment("NATS_URL", nats)
            .WithEnvironment("ADMIN_USER", adminUser) //ui login
            .WithEnvironment("ADMIN_PASS", adminPass) //ui password
            .WithEnvironment("JWT_SECRET", jwtSecret)
            .WithReference(nats);
    }
}