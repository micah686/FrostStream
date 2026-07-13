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
            .WithEndpoint(port: Ports.NatsClient, targetPort: 4222, name: "client")
            .WithHttpEndpoint(port: Ports.NatsMonitor, targetPort: 8222, name: "monitor")
            .WithEndpoint(port: Ports.NatsWebSocket, targetPort: 9222, name: "ws");

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