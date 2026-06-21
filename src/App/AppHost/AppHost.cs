using AppHost;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

#if DEBUG
    //TODO: REMOVE THIS LATER
    Environment.SetEnvironmentVariable("SINGLE_USER_MODE", "true");
#endif

var singleUserMode = IsTruthy(Environment.GetEnvironmentVariable("SINGLE_USER_MODE"));

var configuredStorageRoot = Environment.GetEnvironmentVariable("FROSTSTREAM_STORAGE_ROOT");
var sharedStorageRoot = string.IsNullOrWhiteSpace(configuredStorageRoot)
    ? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "data"))
    : configuredStorageRoot;

if (!Path.IsPathRooted(sharedStorageRoot))
{
    throw new InvalidOperationException(
        $"FROSTSTREAM_STORAGE_ROOT must be an absolute path, but was '{sharedStorageRoot}'.");
}

sharedStorageRoot = Path.GetFullPath(sharedStorageRoot);
Directory.CreateDirectory(sharedStorageRoot);

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
    //.WithDataVolume("nats-data")    // persist JS data across restarts (uses a Docker volume)
    .WithBindMount("./configs/nats/nats-server.conf", "/etc/nats/nats.conf", isReadOnly: true)
    .WithBindMount(websocketCertPath, "/etc/nats/certs/ws-cert.pem", isReadOnly: true)
    .WithBindMount(websocketKeyPath, "/etc/nats/certs/ws-key.pem", isReadOnly: true)
    .WithArgs("-c", "/etc/nats/nats.conf")
    .WithEndpoint(port: 4222, targetPort: 4222, name: "client")
    .WithHttpEndpoint(port: 8222, targetPort: 8222, name: "monitor")
    .WithEndpoint(port: 9222, targetPort: 9222, name: "ws");

var jwt = Environment.GetEnvironmentVariable("JWT_SECRET") ?? Guid.NewGuid().ToString();
var natsUi = builder
    .AddContainer("nats-ui", "klinux/nats-ui:0.4.0")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithEnvironment("PORT", "8080")
    .WithEnvironment("BASE_URL", "http://localhost:8080")
    .WithEnvironment("NATS_URL", nats)
    .WithEnvironment("ADMIN_USER", "admin") //ui login
    .WithEnvironment("ADMIN_PASS", "admin") //ui password
    .WithEnvironment("JWT_SECRET", jwt)
    .WithReference(nats);

#endregion

var postgresUserValue = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
var postgresPasswordValue = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
var postgresUser = builder.AddParameter("postgres-user", postgresUserValue, publishValueAsDefault: false);
var postgresPassword = builder.AddParameter("postgres-password", postgresPasswordValue, publishValueAsDefault: false, secret: true);

var postgres = builder.AddPostgres("postgres", postgresUser, postgresPassword)
    .WithDbGate(); //needs both aspire dbgate and community toolkit postgres extensions to be same version
    //.WithPgAdmin(); // Optional: adds a pgAdmin container for database management
    //.WithDataVolume(); // Persists data between runs
// Add the database
var database = postgres.AddDatabase("froststreamdb");


//ClickHouse (For live-chat)
// Enable one we are getting to the point of implementing live-chat
// var clickhouse = builder.AddClickHouse("clickhouse");
// var clickhousedb = clickhouse.AddDatabase("clickhousedb");

// OpenBAO secret store (Vault-API-compatible). Runs in dev mode with a deterministic
// root token so services can authenticate without an unseal step. Production deployments
// should switch to AppRole + a properly-initialised cluster.
const string baoDevRootToken = "froststream-dev-root";
var openbao = builder
    .AddContainer("openbao", "openbao/openbao", "latest")
    .WithHttpEndpoint(port: 8200, targetPort: 8200, name: "http")
    .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", baoDevRootToken)
    .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
    .WithArgs("server", "-dev", "-dev-root-token-id", baoDevRootToken);

var openbaoEndpoint = openbao.GetEndpoint("http");

// Typesense — typo-tolerant full-text search for the metadata.* schema. Treated as a
// derived projection of Postgres, rebuildable from scratch if the volume is lost.
const string typesenseDevApiKey = "froststream-dev-key";
var typesense = builder
    .AddContainer("typesense", "typesense/typesense", "30.2")
    .WithVolume("typesense-data", "/data")
    .WithEnvironment("TYPESENSE_DATA_DIR", "/data")
    .WithEnvironment("TYPESENSE_API_KEY", typesenseDevApiKey)
    .WithEnvironment("TYPESENSE_ENABLE_CORS", "true")
    .WithHttpEndpoint(port: 8108, targetPort: 8108, name: "http");

var typesenseEndpoint = typesense.GetEndpoint("http");

var authentikAuthority = Environment.GetEnvironmentVariable("AUTHENTIK_AUTHORITY")
    ?? "http://localhost:9000/application/o/froststream/";
var authentikClientSecret = builder.AddParameter(
    "authentik-client-secret",
    Environment.GetEnvironmentVariable("AUTHENTIK_CLIENT_SECRET") ?? "froststream-dev-client-secret",
    publishValueAsDefault: false,
    secret: true);
IResourceBuilder<ContainerResource>? authentik = null;
IResourceBuilder<ContainerResource>? openfga = null;
EndpointReference? openfgaEndpoint = null;

if (!singleUserMode)
{
    var authentikSecretKey = builder.AddParameter(
        "authentik-secret-key",
        Environment.GetEnvironmentVariable("AUTHENTIK_SECRET_KEY") ?? Guid.NewGuid().ToString("N"),
        publishValueAsDefault: false,
        secret: true);
    var authentikBootstrapPassword = builder.AddParameter(
        "authentik-bootstrap-password",
        Environment.GetEnvironmentVariable("AUTHENTIK_BOOTSTRAP_PASSWORD") ?? "froststream-dev-admin",
        publishValueAsDefault: false,
        secret: true);
    var authentikRedis = builder
        .AddContainer("authentik-redis", "redis", "7-alpine")
        .WithEndpoint(targetPort: 6379, name: "tcp");

    authentik = builder
        .AddContainer("authentik", "ghcr.io/goauthentik/server", "latest")
        .WithArgs("server")
        .WithHttpEndpoint(port: 9000, targetPort: 9000, name: "http")
        .WithEnvironment("AUTHENTIK_SECRET_KEY", authentikSecretKey)
        .WithEnvironment("AUTHENTIK_REDIS__HOST", "authentik-redis")
        .WithEnvironment("AUTHENTIK_REDIS__PORT", "6379")
        .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", "postgres")
        .WithEnvironment("AUTHENTIK_POSTGRESQL__PORT", "5432")
        .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", postgresUser)
        .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", postgresPassword)
        .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", "froststreamdb")
        .WithEnvironment("AUTHENTIK_BOOTSTRAP_EMAIL", Environment.GetEnvironmentVariable("AUTHENTIK_BOOTSTRAP_EMAIL") ?? "admin@localhost")
        .WithEnvironment("AUTHENTIK_BOOTSTRAP_PASSWORD", authentikBootstrapPassword)
        .WaitFor(database)
        .WaitFor(authentikRedis);

    builder
        .AddContainer("authentik-worker", "ghcr.io/goauthentik/server", "latest")
        .WithArgs("worker")
        .WithEnvironment("AUTHENTIK_SECRET_KEY", authentikSecretKey)
        .WithEnvironment("AUTHENTIK_REDIS__HOST", "authentik-redis")
        .WithEnvironment("AUTHENTIK_REDIS__PORT", "6379")
        .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", "postgres")
        .WithEnvironment("AUTHENTIK_POSTGRESQL__PORT", "5432")
        .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", postgresUser)
        .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", postgresPassword)
        .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", "froststreamdb")
        .WaitFor(database)
        .WaitFor(authentikRedis);

    var openfgaMigrate = builder
        .AddContainer("openfga-migrate", "openfga/openfga", "latest")
        .WithArgs("migrate")
        .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
        .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgresUser}:{postgresPassword}@postgres:5432/froststreamdb?sslmode=disable")
        .WaitFor(database);

    openfga = builder
        .AddContainer("openfga", "openfga/openfga", "latest")
        .WithArgs("run")
        .WithHttpEndpoint(port: 8081, targetPort: 8080, name: "http")
        .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
        .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgresUser}:{postgresPassword}@postgres:5432/froststreamdb?sslmode=disable")
        .WithEnvironment("OPENFGA_PLAYGROUND_ENABLED", "true")
        .WaitForCompletion(openfgaMigrate)
        .WaitFor(database);
    openfgaEndpoint = openfga.GetEndpoint("http");
}

// projects
var databridge = builder.AddProject<Projects.DataBridge>("databridge")
    .WithReference(database).WaitFor(database)
    .WithReference(nats).WaitFor(nats)
    .WithEnvironment("OpenBao__Address", openbaoEndpoint)
    .WithEnvironment("OpenBao__Token", baoDevRootToken)
    .WithEnvironment("Typesense__Url", typesenseEndpoint)
    .WithEnvironment("Typesense__ApiKey", typesenseDevApiKey)
    .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
    .WithEnvironment("SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WaitFor(openbao)
    .WaitFor(typesense);

var webapi = builder.AddProject<Projects.WebAPI>("webapi", launchProfileName: "https")
    .WithReference(nats).WaitFor(nats)
    .WithEnvironment("OpenBao__Address", openbaoEndpoint)
    .WithEnvironment("OpenBao__Token", baoDevRootToken)
    .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
    .WithEnvironment("SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WithEnvironment("Auth__SingleUserMode", singleUserMode ? "true" : "false")
    .WithEnvironment("Auth__Authority", authentikAuthority)
    .WithEnvironment("Auth__Audience", Environment.GetEnvironmentVariable("AUTHENTIK_API_AUDIENCE") ?? "froststream-api")
    .WithEnvironment("Auth__RequireHttpsMetadata", Environment.GetEnvironmentVariable("AUTH_REQUIRE_HTTPS_METADATA") ?? "false")
    .WithEnvironment("OpenFga__StoreId", Environment.GetEnvironmentVariable("OPENFGA_STORE_ID") ?? "")
    .WithEnvironment("OpenFga__AuthorizationModelId", Environment.GetEnvironmentVariable("OPENFGA_AUTHORIZATION_MODEL_ID") ?? "")
    .WaitFor(openbao);

if (!singleUserMode && authentik is not null && openfga is not null && openfgaEndpoint is not null)
{
    webapi = webapi
        .WithEnvironment("OpenFga__Endpoint", openfgaEndpoint)
        .WaitFor(authentik)
        .WaitFor(openfga);
}

builder.AddViteApp("frontend", "../Frontend")
    .WithPnpm()
    .WithReference(webapi)
    .WithEnvironment("VITE_API_BASE_URL", webapi.GetEndpoint("https"))
    .WithEnvironment("API_BASE_URL", webapi.GetEndpoint("https"))
    .WithEnvironment("SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WithEnvironment("VITE_SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WithEnvironment("VITE_AUTH_MODE", singleUserMode ? "single-user" : "multi-user")
    .WithEnvironment("VITE_AUTH_AUTHORITY", singleUserMode ? "" : authentikAuthority)
    .WithEnvironment("AUTH_AUTHORITY", singleUserMode ? "" : authentikAuthority)
    .WithEnvironment("AUTH_CLIENT_ID", Environment.GetEnvironmentVariable("AUTHENTIK_CLIENT_ID") ?? "froststream-bff")
    .WithEnvironment("AUTH_CLIENT_SECRET", authentikClientSecret)
    .WithEnvironment("AUTH_SCOPES", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups");

builder.AddProject<Projects.Worker>("worker")
    .WithReference(nats).WaitFor(nats)
    .WithEnvironment("OpenBao__Address", openbaoEndpoint)
    .WithEnvironment("OpenBao__Token", baoDevRootToken)
    .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
    .WaitFor(openbao);

builder.AddProject<Projects.Scheduler>("scheduler")
    .WithReference(nats).WaitFor(nats)
    .WaitFor(databridge)
    .WithHttpEndpoint(name: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/quartz";
        url.DisplayText = "Quartz";
    });

builder.Build().Run();

static bool IsTruthy(string? value)
    => value is not null &&
       (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
