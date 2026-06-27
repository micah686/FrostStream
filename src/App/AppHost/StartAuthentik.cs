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
    ReferenceExpression? Authority);

public static class StartAuthentik
{
    // authentik 2025.10+ dropped the Redis requirement; caching, the embedded outpost
    // and WebSocket state are now backed by PostgreSQL. Both server and worker share the
    // existing postgres instance.
    private const string ImageTag = "2026.5.3";
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
            Environment.GetEnvironmentVariable("AUTHENTIK_CLIENT_SECRET") ?? "froststream-dev-client-secret",
            publishValueAsDefault: false,
            secret: true);
        var clientId = Environment.GetEnvironmentVariable("AUTHENTIK_CLIENT_ID") ?? "froststream-bff";

        // Single-user mode runs without an identity provider, so no authentik containers are added.
        if (hardening.SingleUserMode)
        {
            return new AuthentikResources(clientId, clientSecret, configuredAuthority, Server: null, Authority: null);
        }

        var blueprintPath = Path.Combine(
            builder.AppHostDirectory,
            "configs",
            "authentik",
            "blueprints",
            "froststream.yaml");

        var secretKey = builder.AddParameter(
            "authentik-secret-key",
            Environment.GetEnvironmentVariable("AUTHENTIK_SECRET_KEY") ?? Guid.NewGuid().ToString("N"),
            publishValueAsDefault: false,
            secret: true);
        var bootstrapPassword = builder.AddParameter(
            "authentik-bootstrap-password",
            Environment.GetEnvironmentVariable("AUTHENTIK_BOOTSTRAP_PASSWORD") ?? "froststream-dev-admin",
            publishValueAsDefault: false,
            secret: true);
        var signingKeyName = Environment.GetEnvironmentVariable("AUTHENTIK_SIGNING_KEY_NAME");

        // The server serves the web UI and OIDC endpoints; the worker applies blueprints and
        // runs background tasks. Both run the same image with different args and share config.
        var server = builder
            .AddContainer("authentik", "ghcr.io/goauthentik/server", ImageTag)
            .WithArgs("server")
            .WithHttpEndpoint(port: 9000, targetPort: 9000, name: "http")
            .WithEnvironment("AUTHENTIK_SECRET_KEY", secretKey)
            .WithAuthentikPostgresEnv(postgres)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_EMAIL", Environment.GetEnvironmentVariable("AUTHENTIK_BOOTSTRAP_EMAIL") ?? "admin@localhost")
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_PASSWORD", bootstrapPassword)
            .WithEnvironment("AUTHENTIK_CLIENT_ID", clientId)
            .WithEnvironment("AUTHENTIK_CLIENT_SECRET", clientSecret)
            .WithBindMount(blueprintPath, "/blueprints/froststream.yaml", isReadOnly: true)
            .WithHttpHealthCheck(path: "/-/health/ready/")
            .WaitFor(postgres.AuthentikDb);

        if (!string.IsNullOrWhiteSpace(signingKeyName))
        {
            server = server.WithEnvironment("AUTHENTIK_SIGNING_KEY_NAME", signingKeyName);
        }

        var worker = builder
            .AddContainer("authentik-worker", "ghcr.io/goauthentik/server", ImageTag)
            .WithArgs("worker")
            .WithEnvironment("AUTHENTIK_SECRET_KEY", secretKey)
            .WithAuthentikPostgresEnv(postgres)
            .WithEnvironment("AUTHENTIK_CLIENT_ID", clientId)
            .WithEnvironment("AUTHENTIK_CLIENT_SECRET", clientSecret)
            .WithBindMount(blueprintPath, "/blueprints/froststream.yaml", isReadOnly: true)
            .WaitFor(postgres.AuthentikDb);

        if (!string.IsNullOrWhiteSpace(signingKeyName))
        {
            worker = worker.WithEnvironment("AUTHENTIK_SIGNING_KEY_NAME", signingKeyName);
        }

        var authority = ReferenceExpression.Create($"{server.GetEndpoint("http")}/application/o/froststream/");

        return new AuthentikResources(clientId, clientSecret, configuredAuthority, server, authority);
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
