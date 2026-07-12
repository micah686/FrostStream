namespace AppHost;

/// <summary>
/// Resources produced by <see cref="StartAuthentik"/>.
/// <para>
/// <see cref="ClientId"/>, <see cref="ClientSecret"/> and <see cref="ConfiguredAuthority"/> are
/// always populated — the web API and auth-tester need the OIDC client details in both single-
/// and multi-user mode. <see cref="Server"/> and <see cref="Authority"/> are only populated when
/// authentik is actually running (multi-user mode); they are <c>null</c> in single-user mode.
/// </para>
/// </summary>
public sealed record AuthentikResources(
    string ClientId,
    IResourceBuilder<ParameterResource> ClientSecret,
    string? ConfiguredAuthority,
    IResourceBuilder<ContainerResource>? Server,
    ReferenceExpression? Authority,
    IResourceBuilder<ParameterResource>? ApiToken);

public static class StartAuthentik
{
    // authentik 2025.10+ dropped the Redis requirement; caching, the embedded outpost
    // and WebSocket state are now backed by PostgreSQL. Both server and worker share the
    // existing postgres instance.
    private const string DatabaseName = "authentikdb";

    public static AuthentikResources Start(
        IDistributedApplicationBuilder builder,
        PostgresResources postgres,
        AppHostHardeningOptions hardening)
    {
        // Client details and an optional externally-configured authority are needed regardless
        // of mode, so they are resolved before the single-user early-out below.
        var configuredAuthority = Environment.GetEnvironmentVariable("AUTHENTIK_AUTHORITY");
        var clientSecret = builder.AddParameter(
            "authentik-client-secret",
            Helpers.GetEnv("AUTHENTIK_CLIENT_SECRET"),
            publishValueAsDefault: false,
            secret: true);
        var clientId = Helpers.GetEnv("AUTHENTIK_CLIENT_ID");

        // Single-user mode runs without an identity provider, so no authentik containers are added.
        if (hardening.SingleUserMode)
        {
            return new AuthentikResources(clientId, clientSecret, configuredAuthority, Server: null, Authority: null, ApiToken: null);
        }

        var blueprintPath = Path.Combine(
            builder.AppHostDirectory,
            "configs",
            "authentik",
            "blueprints",
            "froststream.yaml");

        var secretKey = builder.AddParameter(
            "authentik-secret-key",
            Helpers.GetEnv("AUTHENTIK_SECRET_KEY"),
            publishValueAsDefault: false,
            secret: true);
        var bootstrapPassword = builder.AddParameter(
            "authentik-bootstrap-password",
            Helpers.GetEnv("AUTHENTIK_BOOTSTRAP_PASSWORD"),
            publishValueAsDefault: false,
            secret: true);
        var signingKeyName = Environment.GetEnvironmentVariable("AUTHENTIK_SIGNING_KEY_NAME");
        // Authentik's bootstrap creates an akadmin API token with this exact value on first start.
        // The WebAPI uses it for directory lookups (grantee autocomplete in bundle management).
        var apiToken = builder.AddParameter(
            "authentik-bootstrap-token",
            Helpers.GetEnv("AUTHENTIK_BOOTSTRAP_TOKEN"),
            publishValueAsDefault: false,
            secret: true);

        // The server serves the web UI and OIDC endpoints; the worker applies blueprints and
        // runs background tasks. Both run the same image with different args and share config.
        var server = builder
            .AddContainer("authentik", "ghcr.io/goauthentik/server", "2026.5.3")
            .WithArgs("server")
            .WithHttpEndpoint(port: 9000, targetPort: 9000, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("AUTHENTIK_SECRET_KEY", secretKey)
            .WithAuthentikPostgresEnv(postgres)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_EMAIL", Helpers.GetEnv("AUTHENTIK_BOOTSTRAP_EMAIL"))
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_PASSWORD", bootstrapPassword)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_TOKEN", apiToken)
            .WithEnvironment("AUTHENTIK_CLIENT_ID", clientId)
            .WithEnvironment("AUTHENTIK_CLIENT_SECRET", clientSecret)
            .WithBindMount(blueprintPath, "/blueprints/froststream.yaml", isReadOnly: true)
            .WithHttpHealthCheck(path: "/-/health/ready/")
            .WaitFor(postgres.AuthentikDb)
            .WaitForDatabases(postgres)
            .WithComposeDependencyCondition("postgres", "service_healthy")
            // Compose has no notion of Aspire health checks, so publish an explicit healthcheck.
            // "ak healthcheck" fails until first-boot migrations finish (verified), letting
            // dependents gate on service_healthy. Generous retries: first boot on slow hosts
            // (Windows/Docker Desktop) migrates for minutes.
            .PublishAsDockerComposeService((_, service) =>
            {
                service.Healthcheck = new()
                {
                    Test = ["CMD", "ak", "healthcheck"],
                    Interval = "10s",
                    Timeout = "30s",
                    Retries = 30,
                    StartPeriod = "60s",
                };
            });

        if (!string.IsNullOrWhiteSpace(signingKeyName))
        {
            server = server.WithEnvironment("AUTHENTIK_SIGNING_KEY_NAME", signingKeyName);
        }

        var worker = builder
            .AddContainer("authentik-worker", "ghcr.io/goauthentik/server", "2026.5.3")
            .WithArgs("worker")
            .WithEnvironment("AUTHENTIK_SECRET_KEY", secretKey)
            .WithAuthentikPostgresEnv(postgres)
            // The worker applies blueprints, so the !Env lookup for the API token resolves here.
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_TOKEN", apiToken)
            .WithEnvironment("AUTHENTIK_CLIENT_ID", clientId)
            .WithEnvironment("AUTHENTIK_CLIENT_SECRET", clientSecret)
            .WithBindMount(blueprintPath, "/blueprints/froststream.yaml", isReadOnly: true)
            .WaitFor(postgres.AuthentikDb)
            .WaitForDatabases(postgres)
            .WithComposeDependencyCondition("postgres", "service_healthy")
            // Server and worker racing lifecycle.migrate on an empty database intermittently
            // fails ("relation authentik_core_group does not exist"), so the worker starts only
            // once the server is healthy — i.e. migrations are done.
            .WaitFor(server)
            .WithComposeDependencyCondition("authentik", "service_healthy");

        if (!string.IsNullOrWhiteSpace(signingKeyName))
        {
            worker = worker.WithEnvironment("AUTHENTIK_SIGNING_KEY_NAME", signingKeyName);
        }

        var authority = ReferenceExpression.Create($"{server.GetEndpoint("http")}/application/o/froststream/");

        return new AuthentikResources(clientId, clientSecret, configuredAuthority, server, authority, apiToken);
    }

    private static IResourceBuilder<ContainerResource> WithAuthentikPostgresEnv(
        this IResourceBuilder<ContainerResource> container,
        PostgresResources postgres)
    {
        return container
            .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", "postgres")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__PORT", "5432")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", postgres.User)
            .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", postgres.Password)
            .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", DatabaseName);
    }
}
