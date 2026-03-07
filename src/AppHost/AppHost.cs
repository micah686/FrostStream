using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Start a local NATS container and enable JetStream
var nats = builder
    .AddNats("nats")                 // logical name "nats"
    .WithJetStream()                 // turn on JetStream
    .WithDataVolume("nats-data")    // persist JS data across restarts (uses a Docker volume)
    .WithArgs("-m", "8222")         // Optional: expose the monitor port for local browsing
    .WithHttpEndpoint(name: "monitor", port: 8222, targetPort: 8222);

// NATS Dashboard (static UI served by Caddy)
var natsDashboard = builder
    .AddContainer("nats-dashboard", "mdawar/nats-dashboard:latest")
    // Option A (recommended): reverse-proxy to nats:8222 from the dashboard container.
    // This avoids browser mixed-content/CORS issues and keeps the URL simple.
    .WithEnvironment("REVERSE_PROXY_UPSTREAM", "nats:8222")
    // Mount a runtime config.json (see below). Optional but nice for defaults.
    .WithBindMount("./configs/nats-dashboard/config.json", "/srv/config.json", isReadOnly: true)
    // Expose the dashboard
    .WithHttpEndpoint(name: "ui", port: 8000, targetPort: 80)
    // Make sure the DNS name "nats" resolves for the proxy
    .WithReference(nats);

var postgres = builder.AddPostgres("postgres")
    //.WithPgAdmin() // Optional: adds a pgAdmin container for database management
    .WithDataVolume(); // Persists data between runs
// Add the database
var database = postgres.AddDatabase("froststreamdb");

// projects
builder.AddProject<Projects.DataBridge>("databridge")
    .WithReference(database).WaitFor(database)
    .WithReference(nats).WaitFor(nats);

builder.AddProject<Projects.WebAPI>("webapi")
    .WithReference(nats).WaitFor(nats);

builder.AddProject<Projects.Worker>("worker")
    .WithReference(nats).WaitFor(nats);

builder.Build().Run();
