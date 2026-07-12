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
            .WithBindMount("./configs/nats/nats-server.conf", "/etc/nats/nats.conf", isReadOnly: true)
            .WithBindMount(websocketCertPath, "/etc/nats/certs/ws-cert.pem", isReadOnly: true)
            .WithBindMount(websocketKeyPath, "/etc/nats/certs/ws-key.pem", isReadOnly: true)
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
        var jwt = Environment.GetEnvironmentVariable("JWT_SECRET") ?? Guid.NewGuid().ToString();
        var natsUi = builder
            .AddContainer("nats-ui", "klinux/nats-ui", "0.4.0")
            .WithHttpEndpoint(port: Ports.NatsUi, targetPort: 8080, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("PORT", "8080")
            .WithEnvironment("BASE_URL", "http://localhost:" + Ports.NatsUi)
            .WithEnvironment("NATS_URL", nats)
            .WithEnvironment("ADMIN_USER", Helpers.GetEnv("NATS_UI_ADMIN_USER")) //ui login
            .WithEnvironment("ADMIN_PASS", Helpers.GetEnv("NATS_UI_ADMIN_PASS")) //ui password
            .WithEnvironment("JWT_SECRET", jwt)
            .WithReference(nats);
    }
}