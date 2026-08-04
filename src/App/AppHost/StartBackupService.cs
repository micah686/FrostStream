namespace AppHost;

public static class StartBackupService
{
    private const string ContainerBackupRoot = "/backups";

    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        PostgresResources postgres,
        OpenBaoResources openBao,
        IResourceBuilder<ParameterResource> openBaoToken)
    {
        var backupRoot = BackupPaths.BackupRoot(sharedStorageRoot);
        Directory.CreateDirectory(backupRoot);

        var backupRootParameter = builder.AddParameter(
            "froststream-backup-root",
            builder.ExecutionContext.IsPublishMode ? "./backups" : backupRoot,
            publishValueAsDefault: true);

        // Static token guarding the break-glass restore wizard; must not depend on Authentik,
        // which is down during a restore. Same minting pattern as media-processor-api-key.
        var restoreUiToken = builder.AddParameter(
            "backup-restore-ui-token",
            Environment.GetEnvironmentVariable("BACKUP_RESTORE_UI_TOKEN") ?? "froststream-dev-restore-token",
            publishValueAsDefault: false,
            secret: true);

        var pgBackRestConf = Path.Combine(builder.AppHostDirectory, "configs", "pgbackrest", "pgbackrest.conf");
        var context = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
        var service = builder
            .AddDockerfile("backupservice", context, "App/BackupService/Dockerfile")
            .WithHttpEndpoint(port: Ports.BackupService, targetPort: 8080, name: "http")
            // Host-published restore wizard; usable while everything except this container is down.
            .WithHttpEndpoint(port: Ports.BackupRestoreUi, targetPort: 8081, name: "restore-ui")
            .WithReference(nats).WaitFor(nats)
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb).WaitForDatabases(postgres)
            .WithEnvironment("Backup__Directory", ContainerBackupRoot)
            .WithEnvironment("Backup__Stanza", "froststream")
            .WithEnvironment("Backup__PgDataPath", "/var/lib/postgresql/18/docker")
            .WithEnvironment("Backup__PostgresHost", postgres.Server.GetEndpoint("tcp").Property(EndpointProperty.Host))
            .WithEnvironment("Backup__PostgresPort", postgres.Server.GetEndpoint("tcp").Property(EndpointProperty.Port))
            .WithEnvironment("Backup__PostgresUser", postgres.User)
            .WithEnvironment("Backup__PostgresPassword", postgres.Password)
            .WithEnvironment("Backup__OpenBaoAddress", openBao.Server.GetEndpoint("http"))
            .WithEnvironment("Backup__OpenBaoToken", openBaoToken)
            .WithEnvironment("Backup__OpenBaoKvMount", "secret")
            .WithEnvironment("Backup__RestoreUiToken", restoreUiToken)
            // Ensures Aspire's compose publisher emits FROSTSTREAM_BACKUP_ROOT in .env.
            .WithEnvironment("Backup__HostRoot", backupRootParameter)
            .WaitForOpenBao(openBao)
            .WithPortableBindMount(
                backupRoot,
                "${FROSTSTREAM_BACKUP_ROOT:-./backups}",
                ContainerBackupRoot)
            .WithPortableBindMount(
                pgBackRestConf,
                "../AppHost/configs/pgbackrest/pgbackrest.conf",
                "/etc/pgbackrest/pgbackrest.conf",
                isReadOnly: true)
            // Shared with the postgres container: pgBackRest backup/restore reads and writes the
            // cluster files directly, and connects over the shared unix socket.
            .WithVolume("froststream-postgres-data", "/var/lib/postgresql")
            .WithVolume("froststream-postgres-socket", "/var/run/postgresql");

        service.WithEndpoint("restore-ui", endpoint =>
        {
            endpoint.IsExternal = true;
            if (builder.ExecutionContext.IsPublishMode)
            {
                endpoint.Port = Ports.BackupRestoreUi;
            }
        }, createIfNotExists: false);

        service.PublishAsDockerComposeService((_, compose) =>
        {
            compose.Image = "localhost/froststream-backupservice:latest";
            compose.PullPolicy = "build";
            compose.Build = new Aspire.Hosting.Docker.Resources.ServiceNodes.Build
            {
                Context = "../..",
                Dockerfile = "App/BackupService/Dockerfile"
            };
            compose.Healthcheck = new()
            {
                Test = ["CMD-SHELL", "curl -fsS http://localhost:8080/health || exit 1"],
                Interval = "10s",
                Timeout = "5s",
                Retries = 12,
                StartPeriod = "20s"
            };
        });
        service.WithComposeDependencyCondition("openbao", "service_healthy");

        return service;
    }
}
