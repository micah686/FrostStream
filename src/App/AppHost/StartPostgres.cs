namespace AppHost;

public sealed record PostgresResources(
    IResourceBuilder<ParameterResource> User,
    IResourceBuilder<ParameterResource> Password,
    IResourceBuilder<PostgresServerResource> Server,
    IResourceBuilder<PostgresDatabaseResource> FrostStreamDb,
    IResourceBuilder<PostgresDatabaseResource> AuthentikDb,
    IResourceBuilder<PostgresDatabaseResource> OpenFgaDb,
    IResourceBuilder<ContainerResource>? Init);

public static class PostgresInitExtensions
{
    /// <summary>
    /// Waits for the publish-only postgres-init seeding container so the databases created by
    /// <c>AddDatabase</c> exist before the consumer starts. No-op in run mode, where Aspire
    /// creates the databases itself.
    /// </summary>
    public static IResourceBuilder<T> WaitForDatabases<T>(
        this IResourceBuilder<T> resource,
        PostgresResources postgres)
        where T : IResourceWithWaitSupport
        => postgres.Init is null ? resource : resource.WaitForCompletion(postgres.Init);
}

public static class StartPostgres
{
    public static PostgresResources Start(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot)
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

        // Shared WAL archive store: written by the server's archive_command (mounted at
        // /wal-archive) and read by BackupTool for wal-archive verification / PITR restore.
        // Made world-writable so the container's postgres user can write regardless of the
        // rootless-podman uid mapping.
        var walArchiveDir = BackupPaths.WalArchiveDirectory(sharedStorageRoot);
        Directory.CreateDirectory(walArchiveDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                walArchiveDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        var postgresConf = Path.Combine(builder.AppHostDirectory, "configs", "postgres", "postgresql.conf");

        // WithDbGate requires CommunityToolkit.Aspire.Hosting.DbGate and
        // CommunityToolkit.Aspire.Hosting.PostgreSQL.Extensions at the same version.
        var server = builder.AddPostgres("postgres", user, password)
            .WithDbGate()
            .WithDataVolume()
            .WithBindMount(postgresConf, "/etc/postgresql/postgresql.conf", isReadOnly: true)
            .WithBindMount(walArchiveDir, "/wal-archive")
            .WithArgs("-c", "config_file=/etc/postgresql/postgresql.conf");

        // In run mode Aspire creates the AddDatabase databases itself, but the published compose
        // file has no such mechanism: the postgres image only creates the default POSTGRES_USER
        // database, so authentik/openfga/the app all crash-loop on a fresh volume. Publish a
        // one-shot seeding container that consumers gate on via WaitForDatabases.
        IResourceBuilder<ContainerResource>? init = null;
        if (builder.ExecutionContext.IsPublishMode)
        {
            const string seedScript = """
                set -eu
                until pg_isready -q; do echo 'postgres-init: waiting for postgres'; sleep 1; done
                for db in froststreamdb authentikdb openfgadb; do
                  if [ "$(psql -tAc "SELECT 1 FROM pg_database WHERE datname = '$db'")" = '1' ]; then
                    echo "postgres-init: database $db already exists"
                  else
                    echo "postgres-init: creating database $db"
                    createdb "$db"
                  fi
                done
                echo 'postgres-init: done'
                """;

            init = builder
                .AddContainer("postgres-init", "docker.io/library/postgres", "18.3")
                .WithEntrypoint("/bin/bash")
                .WithArgs("-c", seedScript)
                .WithEnvironment("PGHOST", "postgres")
                .WithEnvironment("PGPORT", "5432")
                .WithEnvironment("PGDATABASE", "postgres")
                .WithEnvironment("PGUSER", user)
                .WithEnvironment("PGPASSWORD", password)
                .WaitFor(server);
        }

        return new PostgresResources(
            user,
            password,
            server,
            server.AddDatabase("froststreamdb"),
            server.AddDatabase("authentikdb"),
            server.AddDatabase("openfgadb"),
            init);
    }
}
