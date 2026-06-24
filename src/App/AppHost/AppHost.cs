using AppHost;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// #if DEBUG
//     //TODO: REMOVE THIS LATER
//     Environment.SetEnvironmentVariable("SINGLE_USER_MODE", "true");
// #endif

var singleUserMode = IsTruthy(Environment.GetEnvironmentVariable("SINGLE_USER_MODE"));
var hardening = AppHostHardening.Read(singleUserMode);
AppHostHardening.Validate(hardening);
var webApiEndpointName = hardening.EnableHttps ? "https" : "http";

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


var nats = StartNats.Start(builder);


var postgresUserValue = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
var postgresPasswordValue = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
var postgresUser = builder.AddParameter("postgres-user", postgresUserValue, publishValueAsDefault: false);
var postgresPassword = builder.AddParameter("postgres-password", postgresPasswordValue, publishValueAsDefault: false, secret: true);

var postgres = builder.AddPostgres("postgres", postgresUser, postgresPassword)
    .WithDbGate(); //needs both aspire dbgate and community toolkit postgres extensions to be same version
    //.WithPgAdmin(); // Optional: adds a pgAdmin container for database management
    //.WithDataVolume(); // Persists data between runs
// Add the databases. App databases share the same PostgreSQL server, but Authentik and OpenFGA keep
// their own logical databases so backups and future moves can be scoped independently.
var database = postgres.AddDatabase("froststreamdb");
var authentikDatabase = postgres.AddDatabase("authentikdb");
var openFgaDatabase = postgres.AddDatabase("openfgadb");


//ClickHouse (For live-chat)
// Enable one we are getting to the point of implementing live-chat
// var clickhouse = builder.AddClickHouse("clickhouse");
// var clickhousedb = clickhouse.AddDatabase("clickhousedb");

// OpenBAO secret store (Vault-API-compatible). Runs in dev mode with a deterministic
// root token so services can authenticate without an unseal step. Production deployments
// should switch to AppRole + a properly-initialised cluster.
var openbao = builder
    .AddContainer("openbao", "openbao/openbao", hardening.OpenBaoImageTag)
    .WithHttpEndpoint(port: 8200, targetPort: 8200, name: "http")
    .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", hardening.OpenBaoToken)
    .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
    .WithArgs("server", "-dev", "-dev-root-token-id", hardening.OpenBaoToken);

var openbaoEndpoint = openbao.GetEndpoint("http");

// Typesense — typo-tolerant full-text search for the metadata.* schema. Treated as a
// derived projection of Postgres, rebuildable from scratch if the volume is lost.
var typesense = builder
    .AddContainer("typesense", "typesense/typesense", "30.2")
    .WithVolume("typesense-data", "/data")
    .WithEnvironment("TYPESENSE_DATA_DIR", "/data")
    .WithEnvironment("TYPESENSE_API_KEY", hardening.TypesenseApiKey)
    .WithEnvironment("TYPESENSE_ENABLE_CORS", "true")
    .WithHttpEndpoint(port: 8108, targetPort: 8108, name: "http");

var typesenseEndpoint = typesense.GetEndpoint("http");

var authentik = StartAuthentik.Start(builder, singleUserMode, postgresUser, postgresPassword, authentikDatabase, "authentikdb");

IResourceBuilder<ContainerResource>? openfga = null;
EndpointReference? openfgaEndpoint = null;

if (!singleUserMode)
{
    var openfgaMigrate = builder
        .AddContainer("openfga-migrate", "openfga/openfga", hardening.OpenFgaImageTag)
        .WithArgs("migrate")
        .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
        .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgresUser}:{postgresPassword}@postgres:5432/openfgadb?sslmode=disable")
        .WaitFor(openFgaDatabase);

    openfga = builder
        .AddContainer("openfga", "openfga/openfga", hardening.OpenFgaImageTag)
        .WithArgs("run")
        .WithHttpEndpoint(port: 8081, targetPort: 8080, name: "http")
        .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
        .WithEnvironment("OPENFGA_DATASTORE_URI", $"postgres://{postgresUser}:{postgresPassword}@postgres:5432/openfgadb?sslmode=disable")
        .WithEnvironment("OPENFGA_PLAYGROUND_ENABLED", hardening.EnableFgaAuthenticatedEndpoints ? "false" : "true")
        .WaitForCompletion(openfgaMigrate)
        .WaitFor(openFgaDatabase);

    if (hardening.EnableFgaAuthenticatedEndpoints)
    {
        openfga = openfga
            .WithEnvironment("OPENFGA_AUTHN_METHOD", "preshared")
            .WithEnvironment("OPENFGA_AUTHN_PRESHARED_KEYS", hardening.OpenFgaApiToken);
    }

    openfgaEndpoint = openfga.GetEndpoint("http");
}

// projects
var databridge = builder.AddProject<Projects.DataBridge>("databridge")
    .WithReference(database).WaitFor(database)
    .WithReference(nats).WaitFor(nats)
    .WithEnvironment("OpenBao__Address", openbaoEndpoint)
    .WithEnvironment("OpenBao__Token", hardening.OpenBaoToken)
    .WithEnvironment("Typesense__Url", typesenseEndpoint)
    .WithEnvironment("Typesense__ApiKey", hardening.TypesenseApiKey)
    .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
    .WithEnvironment("SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WaitFor(openbao)
    .WaitFor(typesense);

var webapi = builder.AddProject<Projects.WebAPI>("webapi", launchProfileName: webApiEndpointName)
    .WithReference(nats).WaitFor(nats)
    .WithEnvironment("OpenBao__Address", openbaoEndpoint)
    .WithEnvironment("OpenBao__Token", hardening.OpenBaoToken)
    .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
    .WithEnvironment("SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WithEnvironment("Auth__SingleUserMode", singleUserMode ? "true" : "false")
    .WithEnvironment("Auth__AllowSingleUserModeInProduction", Environment.GetEnvironmentVariable("AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION") ?? "false")
    .WithEnvironment("Auth__Audience", Environment.GetEnvironmentVariable("AUTHENTIK_API_AUDIENCE") ?? "froststream-api")
    .WithEnvironment("Auth__ClientId", authentik.ClientId)
    .WithEnvironment("Auth__RequireHttpsMetadata", hardening.RequireHttpsMetadata ? "true" : "false")
    .WithEnvironment("Auth__ExposeOpenApi", Environment.GetEnvironmentVariable("AUTH_EXPOSE_OPENAPI") ?? "false")
    .WithEnvironment("OpenFga__StoreId", Environment.GetEnvironmentVariable("OPENFGA_STORE_ID") ?? "")
    .WithEnvironment("OpenFga__AuthorizationModelId", Environment.GetEnvironmentVariable("OPENFGA_AUTHORIZATION_MODEL_ID") ?? "")
    .WithEnvironment("OpenFga__AutoProvision", Environment.GetEnvironmentVariable("OPENFGA_AUTO_PROVISION") ?? "true")
    .WithEnvironment("OpenFga__BootstrapOwnerSubjects", Environment.GetEnvironmentVariable("OPENFGA_BOOTSTRAP_OWNER_SUB") ?? "")
    .WaitFor(openbao);

webapi = WithAuthAuthority(webapi, "Auth__Authority");

if (!singleUserMode && authentik.Server is { } authentikServer && openfga is not null && openfgaEndpoint is not null)
{
    webapi = webapi
        .WithEnvironment("OpenFga__Endpoint", openfgaEndpoint)
        .WaitFor(authentikServer)
        .WaitFor(openfga);

    if (hardening.EnableFgaAuthenticatedEndpoints)
    {
        webapi = webapi.WithEnvironment("OpenFga__ApiToken", hardening.OpenFgaApiToken);
    }
}

// builder.AddViteApp("frontend", "../Frontend")
//     .WithPnpm()
//     .WithReference(webapi)
//     .WithEnvironment("VITE_API_BASE_URL", webapi.GetEndpoint("https"))
//     .WithEnvironment("API_BASE_URL", webapi.GetEndpoint("https"))
//     .WithEnvironment("SINGLE_USER_MODE", singleUserMode ? "true" : "false")
//     .WithEnvironment("VITE_SINGLE_USER_MODE", singleUserMode ? "true" : "false")
//     .WithEnvironment("VITE_AUTH_MODE", singleUserMode ? "single-user" : "multi-user")
//     .WithEnvironment("AUTH_CLIENT_ID", authentik.ClientId)
//     .WithEnvironment("AUTH_CLIENT_SECRET", authentik.ClientSecret)
//     .WithEnvironment("AUTH_SCOPES", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups");

var authTester = builder.AddViteApp("auth-tester", "../../HelperTestingApps/AuthTester")
    .WithPnpm()
    .WithReference(webapi)
    .WaitFor(webapi)
    .WithEnvironment("VITE_API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
    .WithEnvironment("API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
    .WithEnvironment("SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WithEnvironment("VITE_SINGLE_USER_MODE", singleUserMode ? "true" : "false")
    .WithEnvironment("VITE_AUTH_MODE", singleUserMode ? "single-user" : "multi-user")
    .WithEnvironment("AUTH_CLIENT_ID", authentik.ClientId)
    .WithEnvironment("AUTH_CLIENT_SECRET", authentik.ClientSecret)
    .WithEnvironment("AUTH_SCOPES", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups");

authTester = WithAuthAuthority(authTester, "VITE_AUTH_AUTHORITY");
authTester = WithAuthAuthority(authTester, "AUTH_AUTHORITY");

if (!singleUserMode && authentik.Server is { } authentikServerForTester)
{
    authTester = authTester.WaitFor(authentikServerForTester);
}

builder.AddProject<Projects.Worker>("worker")
    .WithReference(nats).WaitFor(nats)
    .WithEnvironment("OpenBao__Address", openbaoEndpoint)
    .WithEnvironment("OpenBao__Token", hardening.OpenBaoToken)
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

IResourceBuilder<T> WithAuthAuthority<T>(IResourceBuilder<T> resource, string name)
    where T : IResourceWithEnvironment
{
    if (singleUserMode)
    {
        return resource.WithEnvironment(name, "");
    }

    var configuredAuthority = authentik.ConfiguredAuthority;
    if (!string.IsNullOrWhiteSpace(configuredAuthority))
    {
        return resource.WithEnvironment(name, configuredAuthority);
    }

    var authority = authentik.Authority;
    return authority is null
        ? resource.WithEnvironment(name, "")
        : resource.WithEnvironment(name, authority);
}

static bool IsTruthy(string? value)
    => value is not null &&
       (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
