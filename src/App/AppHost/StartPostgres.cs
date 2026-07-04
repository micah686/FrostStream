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

        return new PostgresResources(
            user,
            password,
            server,
            server.AddDatabase("froststreamdb"),
            server.AddDatabase("authentikdb"),
            server.AddDatabase("openfgadb"));
    }
}
