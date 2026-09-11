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
        var deployment = DeploymentRuntime.Current;
        var includeInitialization = deployment.Selection.Profile.IncludeInitialization;
        var user = builder.AddParameter(
            "postgres-user",
            Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres",
            publishValueAsDefault: false);
        var password = builder.AddParameter(
            "postgres-password",
            Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres",
            publishValueAsDefault: false,
            secret: true);

        // Shared backup root: the pgBackRest repository written by the server's archive_command
        // (archive-push) and by BackupService backups, plus the per-backup OpenBao KV exports.
        // Made world-writable so the containers' postgres user (uid 999) can write regardless of
        // the rootless-podman uid mapping.
        var backupRoot = BackupPaths.BackupRoot(sharedStorageRoot);
        if (builder.ExecutionContext.IsRunMode)
        {
            foreach (var dir in new[]
                     {
                         backupRoot,
                         BackupPaths.PgBackRestRepoDirectory(sharedStorageRoot),
                         BackupPaths.OpenBaoExportDirectory(sharedStorageRoot),
                     })
            {
                Directory.CreateDirectory(dir);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        dir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                }
            }
        }

        var postgresConf = deployment.Paths.AppHostConfig("postgres", "postgresql.conf");
        var postgresHba = deployment.Paths.AppHostConfig("postgres", "pg_hba.conf");
        var pgBackRestConf = deployment.Paths.AppHostConfig("pgbackrest", "pgbackrest.conf");
        var imageContext = deployment.Paths.SourceRoot;

        // WithDbGate requires CommunityToolkit.Aspire.Hosting.DbGate and
        // CommunityToolkit.Aspire.Hosting.PostgreSQL.Extensions at the same version.
        var server = builder.AddPostgres(deployment.Names.Postgres, user, password)
            .WithHostPort(Ports.Postgres)
            // stock postgres + pgbackrest, so archive_command can push WAL into the shared repo.
            .WithDockerfile(imageContext, "App/PostgresServer/Dockerfile")
            // Explicitly named so the backupservice container can mount the same volume for
            // pgBackRest backup/restore. (Pre-rework installs used an auto-generated name;
            // `podman volume rename` the old volume or start from a fresh database.)
            .WithDataVolume(deployment.Names.Volume("postgres-data"))
            // Unix-socket volume shared with backupservice: pgBackRest's "local" mode connects
            // to PostgreSQL over the socket.
            .WithVolume(deployment.Names.Volume("postgres-socket"), "/var/run/postgresql")
            .WithPortableBindMount(postgresConf, "../AppHost/configs/postgres/postgresql.conf", "/etc/postgresql/postgresql.conf", isReadOnly: true)
            .WithPortableBindMount(postgresHba, "../AppHost/configs/postgres/pg_hba.conf", "/etc/postgresql/pg_hba.conf", isReadOnly: true)
            .WithPortableBindMount(pgBackRestConf, "../AppHost/configs/pgbackrest/pgbackrest.conf", "/etc/pgbackrest/pgbackrest.conf", isReadOnly: true)
            .WithPortableBindMount(backupRoot, "${FROSTSTREAM_BACKUP_ROOT:-./backups}", "/backups")
            .WithArgs("-c", "config_file=/etc/postgresql/postgresql.conf");

        // The toolkit's DbGate resource never makes it into the compose publish, and its "dbgate"
        // name would collide with the explicit publish-only container below — so run mode only.
        if (builder.ExecutionContext.IsRunMode && Helpers.DevelopmentToolsEnabled)
        {
            server.WithDbGate(dbGate => dbGate.WithHostPort(Ports.DbGate));
        }

        // In run mode Aspire creates the AddDatabase databases itself, but the published compose
        // file has no such mechanism: the postgres image only creates the default POSTGRES_USER
        // database, so authentik/openfga/the app all crash-loop on a fresh volume. Publish a
        // one-shot seeding container that consumers gate on via WaitForDatabases.
        IResourceBuilder<ContainerResource>? init = null;
        if (builder.ExecutionContext.IsPublishMode)
        {
            // Pin the published host port so compose exposes postgres deterministically
            // (WithHostPort above only covers run mode).
            server.WithEndpoint("tcp", endpoint =>
            {
                endpoint.Port = Ports.Postgres;
                endpoint.IsExternal = true;
            }, createIfNotExists: false);

            // pg_isready only checks TCP; this healthcheck also verifies the auth flow works,
            // so dependents using service_healthy don't start before postgres can serve queries.
            // Image/Build mirror WithLocalComposeBuild in StartServices: the compose deployment
            // builds the pgbackrest-enabled server image from the repo checkout.
            server.PublishAsDockerComposeService((_, svc) =>
            {
                svc.Image = deployment.Images.Application(deployment.Names.Postgres);
                svc.PullPolicy = "build";
                svc.Build = new Aspire.Hosting.Docker.Resources.ServiceNodes.Build
                {
                    Context = deployment.Paths.ComposeBuildContext,
                    Dockerfile = deployment.Paths.ComposeDockerfile("PostgresServer")
                };
                svc.Healthcheck = new()
                {
                    Test = ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER} -d postgres"],
                    Interval = "5s",
                    Timeout = "5s",
                    Retries = 10,
                    StartPeriod = "20s",
                };
                svc.Restart = "unless-stopped";
            });

            if (includeInitialization)
            {
                // The compose bind-mounted backup directory may be root-owned on first start.
                var backupInit = builder
                    .AddContainer(deployment.Names.BackupInit, deployment.Images.Postgres.Repository, deployment.Images.Postgres.Tag)
                    .WithEntrypoint("/bin/bash")
                    .WithArgs("-c", "mkdir -p /backups/pgbackrest /backups/openbao /backups/jobs && chown -R 999:999 /backups && echo 'backup-init: done'")
                    .WithPortableBindMount(
                        backupRoot,
                        "${FROSTSTREAM_BACKUP_ROOT:-./backups}",
                        "/backups")
                    .PublishAsDockerComposeService((_, service) => service.Restart = "no");
                server
                    .WaitForCompletion(backupInit)
                    .WithComposeDependencyCondition(deployment.Names.BackupInit, "service_completed_successfully");
            }

            // WithDbGate (run mode, above) is excluded from the compose publish by the community
            // toolkit, so publish a plain dbgate container with the same connection wiring.
            if (Helpers.DevelopmentToolsEnabled)
            {
                builder
                .AddContainer(deployment.Names.DbGate, deployment.Images.DbGate.Repository, deployment.Images.DbGate.Tag)
                .WithHttpEndpoint(port: Ports.DbGate, targetPort: 3000, name: "http")
                .WithExternalHttpEndpoints()
                .WithEnvironment("CONNECTIONS", "con1")
                .WithEnvironment("LABEL_con1", "postgres")
                .WithEnvironment("SERVER_con1", deployment.Names.Postgres)
                .WithEnvironment("USER_con1", user)
                .WithEnvironment("PASSWORD_con1", password)
                .WithEnvironment("PORT_con1", "5432")
                .WithEnvironment("ENGINE_con1", "postgres@dbgate-plugin-postgres")
                .WaitFor(server);
            }

            // ReplaceLineEndings: raw string literals on Windows have CRLF; bash rejects \r.
            var seedScript = """
                set -eu
                ready=false
                attempt=1
                while [ "$attempt" -le 120 ]; do
                  if pg_isready -q; then ready=true; break; fi
                  echo "postgres-init: waiting for postgres ($attempt/120)"
                  sleep 1
                  attempt=$((attempt + 1))
                done
                [ "$ready" = true ] || { echo 'postgres-init: postgres was not ready within 120 seconds' >&2; exit 1; }
                for db in froststreamdb authentikdb openfgadb; do
                  if [ "$(psql -tAc "SELECT 1 FROM pg_database WHERE datname = '$db'")" = '1' ]; then
                    echo "postgres-init: database $db already exists"
                  else
                    echo "postgres-init: creating database $db"
                    createdb "$db"
                  fi
                done
                echo 'postgres-init: done'
                """
                .Replace("froststreamdb authentikdb openfgadb", string.Join(' ',
                    deployment.Names.FrostStreamDatabase,
                    deployment.Names.AuthentikDatabase,
                    deployment.Names.OpenFgaDatabase), StringComparison.Ordinal)
                .ReplaceLineEndings("\n");

            if (includeInitialization)
            {
                init = builder
                    .AddContainer(deployment.Names.PostgresInit, deployment.Images.Postgres.Repository, deployment.Images.Postgres.Tag)
                    .WithEntrypoint("/bin/bash")
                    .WithArgs("-c", seedScript)
                    .WithEnvironment("PGHOST", deployment.Names.Postgres)
                    .WithEnvironment("PGPORT", "5432")
                    .WithEnvironment("PGDATABASE", "postgres")
                    .WithEnvironment("PGUSER", user)
                    .WithEnvironment("PGPASSWORD", password)
                    .WaitFor(server)
                    .WithComposeDependencyCondition(deployment.Names.Postgres, "service_healthy")
                    .PublishAsDockerComposeService((_, service) => service.Restart = "no");
            }
        }

        return new PostgresResources(
            user,
            password,
            server,
            server.AddDatabase(deployment.Names.FrostStreamDatabase),
            server.AddDatabase(deployment.Names.AuthentikDatabase),
            server.AddDatabase(deployment.Names.OpenFgaDatabase),
            init);
    }
}
