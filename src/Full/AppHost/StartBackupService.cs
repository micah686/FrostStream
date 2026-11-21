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
        IResourceBuilder<ParameterResource> openBaoToken,
        IResourceBuilder<ParameterResource> backupApiToken)
    {
        var backupRoot = BackupPaths.BackupRoot(sharedStorageRoot);
        Directory.CreateDirectory(backupRoot);

        var backupRootParameter = builder.AddParameter(
            "froststream-backup-root",
            builder.ExecutionContext.IsPublishMode ? "./backups" : backupRoot,
            publishValueAsDefault: true);

        var pgBackRestConf = Path.Combine(builder.AppHostDirectory, "configs", "pgbackrest", "pgbackrest.conf");
        // The central package/build props live at the repository root; Dockerfile source
        // paths are rooted under src/.
        var context = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".."));
        var service = builder
            .AddDockerfile("backupservice", context, "src/Full/Hosts/BackupServiceNew/Dockerfile")
            .WithHttpEndpoint(port: Ports.BackupService, targetPort: 8080, name: "http")
            // Host-published restore wizard; usable while everything except this container is down.
            .WithHttpEndpoint(port: Ports.BackupRestoreUi, targetPort: 8081, name: "restore-ui")
            .WithReference(nats)
            .WithEnvironment("Backup__Directory", ContainerBackupRoot)
            .WithEnvironment("Backup__Stanza", "main")
            .WithEnvironment("Backup__ApiToken", backupApiToken)
            .WithEnvironment("Backup__PgDataPath", "/var/lib/postgresql/18/docker")
            .WithEnvironment("Backup__PostgresHost", postgres.Server.GetEndpoint("tcp").Property(EndpointProperty.Host))
            .WithEnvironment("Backup__PostgresPort", "5432")
            .WithEnvironment("Backup__PostgresUser", postgres.User)
            .WithEnvironment("PGBACKREST_PG1_USER", postgres.User)
            .WithEnvironment("PGPASSWORD", postgres.Password)
            .WithEnvironment("Backup__OpenBaoAddress", openBao.Server.GetEndpoint("http"))
            .WithEnvironment("Backup__OpenBaoToken", openBaoToken)
            .WithEnvironment("Backup__OpenBaoKvMount", "secret")
            .WithEnvironment("Backup__OpenBaoKeysPath", "/backup-sources/openbao-keys")
            .WithEnvironment("Backup__DataProtectionKeysPath", "/backup-sources/dataprotection")
            .WithPortableBindMount(OpenBaoBootstrapPaths.HostRoot(sharedStorageRoot, builder.AppHostDirectory),
                "${FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT:-./openbao-bootstrap}", "/backup-sources/openbao-keys", isReadOnly: true)
            // Ensures Aspire's compose publisher emits FROSTSTREAM_BACKUP_ROOT in .env.
            .WithEnvironment("Backup__HostRoot", backupRootParameter)
            .WithPortableBindMount(
                backupRoot,
                "${FROSTSTREAM_BACKUP_ROOT:-./backups}",
                ContainerBackupRoot)
            .WithPortableBindMount(
                pgBackRestConf,
                "../../Full/AppHost/configs/pgbackrest/pgbackrest.conf",
                "/etc/pgbackrest/pgbackrest.conf",
                isReadOnly: true)
            // Shared with the postgres container: pgBackRest backup/restore reads and writes the
            // cluster files directly, and connects over the shared unix socket.
            .WithVolume("froststream-postgres-data", "/var/lib/postgresql")
            .WithVolume("froststream-postgres-socket", "/var/run/postgresql");

        if (builder.ExecutionContext.IsPublishMode)
            service.WithVolume("froststream-data-protection-keys", "/backup-sources/dataprotection", isReadOnly: true);
        else
            service.WithBindMount(Path.Combine(sharedStorageRoot, "data-protection-keys"), "/backup-sources/dataprotection", isReadOnly: true);

        var restoreToken = Environment.GetEnvironmentVariable("BACKUP_RESTORE_UI_TOKEN");
        if (builder.ExecutionContext.IsPublishMode && !string.IsNullOrWhiteSpace(restoreToken))
        {
            var parameter = builder.AddParameter("backup-restore-ui-token", restoreToken, publishValueAsDefault: false, secret: true);
            service.WithEnvironment("BACKUP_RESTORE_UI_TOKEN", parameter);
        }
        else
        {
            service.WithEnvironment("BACKUP_RESTORE_UI_TOKEN", builder.ExecutionContext.IsPublishMode
                ? "${BACKUP_RESTORE_UI_TOKEN:-}"
                : restoreToken ?? "");
        }

        if (builder.Resources.OfType<ContainerResource>().FirstOrDefault(resource => resource.Name == "backup-init") is { } backupInit)
        {
            service.WaitForCompletion(builder.CreateResourceBuilder(backupInit));
            service.WithComposeDependencyCondition("backup-init", "service_completed_successfully");
        }

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
                Context = "../../..",
                Dockerfile = "src/Full/Hosts/BackupServiceNew/Dockerfile"
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

        return service;
    }
}
