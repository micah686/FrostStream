using AppHost;
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

#region NATS
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
    .WithDataVolume("nats-data")    // persist JS data across restarts (uses a Docker volume)
    .WithBindMount("./configs/nats/nats-server.conf", "/etc/nats/nats.conf", isReadOnly: true)
    .WithBindMount(websocketCertPath, "/etc/nats/certs/ws-cert.pem", isReadOnly: true)
    .WithBindMount(websocketKeyPath, "/etc/nats/certs/ws-key.pem", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats.conf")
    .WithEndpoint(port: 4222, targetPort: 4222, name: "client")
    .WithHttpEndpoint(port: 8222, targetPort: 8222, name: "monitor")
    .WithEndpoint(port: 9222, targetPort: 9222, name: "ws");

var jwt = Environment.GetEnvironmentVariable("JWT_SECRET") ?? Guid.NewGuid().ToString();
var natsUi = builder
    .AddContainer("nats-ui", "klinux/nats-ui:0.3.7")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithEnvironment("PORT", "8080")
    .WithEnvironment("BASE_URL", "http://localhost:8080")
    .WithEnvironment("NATS_URL", nats)
    .WithEnvironment("ADMIN_USER", "admin") //ui login
    .WithEnvironment("ADMIN_PASS", "admin") //ui password
    .WithEnvironment("JWT_SECRET", jwt)
    .WithReference(nats);

#endregion

var postgres = builder.AddPostgres("postgres")
    .WithDbGate(); //needs both aspire dbgate and community toolkit postgres extensions to be same version
    //.WithPgAdmin(); // Optional: adds a pgAdmin container for database management
    //.WithDataVolume(); // Persists data between runs
// Add the database
var database = postgres.AddDatabase("froststreamdb");

//ClickHouse (For live-chat)
var clickhouse = builder.AddClickHouse("clickhouse");
var clickhousedb = clickhouse.AddDatabase("clickhousedb");

// projects
builder.AddProject<Projects.DataBridge>("databridge")
    .WithReference(database).WaitFor(database)
    .WithReference(nats).WaitFor(nats);

builder.AddProject<Projects.WebAPI>("webapi")
    .WithReference(nats).WaitFor(nats);

builder.AddProject<Projects.Worker>("worker")
    .WithReference(nats).WaitFor(nats);

builder.Build().Run();
