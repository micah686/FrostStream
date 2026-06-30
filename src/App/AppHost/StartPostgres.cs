namespace AppHost;

public sealed record PostgresResources(
    IResourceBuilder<ParameterResource> User,
    IResourceBuilder<ParameterResource> Password,
    IResourceBuilder<PostgresServerResource> Server,
    IResourceBuilder<PostgresDatabaseResource> FrostStreamDb,
    IResourceBuilder<PostgresDatabaseResource> AuthentikDb,
    IResourceBuilder<PostgresDatabaseResource> OpenFgaDb);

public static class StartPostgres
{
    public static PostgresResources Start(IDistributedApplicationBuilder builder, AppHostHardeningOptions hardening)
    {
        var user = builder.AddParameter(
            "postgres-user",
            Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres",
            publishValueAsDefault: false);
        var password = builder.AddParameter(
            "postgres-password",
            Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres",
            publishValueAsDefault: false,
            secret: true);

        // WithDbGate requires CommunityToolkit.Aspire.Hosting.DbGate and
        // CommunityToolkit.Aspire.Hosting.PostgreSQL.Extensions at the same version.
        var server = builder.AddPostgres("postgres", user, password)
            .WithDbGate();

        return new PostgresResources(
            user,
            password,
            server,
            server.AddDatabase("froststreamdb"),
            server.AddDatabase("authentikdb"),
            server.AddDatabase("openfgadb"));
    }
}
