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
        Directory.CreateDirectory(BackupPaths.WalArchiveDirectory(sharedStorageRoot));

        var backupRootParameter = builder.AddParameter(
            "froststream-backup-root",
            builder.ExecutionContext.IsPublishMode ? "./backups" : backupRoot,
            publishValueAsDefault: true);

        var context = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
        var service = builder
            .AddDockerfile("backupservice", context, "App/BackupService/Dockerfile")
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithReference(nats).WaitFor(nats)
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb).WaitForDatabases(postgres)
            .WithEnvironment("Backup__Directory", ContainerBackupRoot)
            .WithEnvironment("Backup__ArchiveDir", $"{ContainerBackupRoot}/wal")
            .WithEnvironment("Backup__PostgresHost", postgres.Server.GetEndpoint("tcp").Property(EndpointProperty.Host))
            .WithEnvironment("Backup__PostgresPort", postgres.Server.GetEndpoint("tcp").Property(EndpointProperty.Port))
            .WithEnvironment("Backup__PostgresUser", postgres.User)
            .WithEnvironment("Backup__PostgresPassword", postgres.Password)
            .WithEnvironment("Backup__OpenBaoAddress", openBao.Server.GetEndpoint("http"))
            .WithEnvironment("Backup__OpenBaoToken", openBaoToken)
            .WithEnvironment("Backup__OpenBaoKvMount", "secret")
            .WithEnvironment("Backup__ScheduledRetentionCount", "14")
            .WithEnvironment("POSTGRES_HOST", postgres.Server.GetEndpoint("tcp").Property(EndpointProperty.Host))
            .WithEnvironment("POSTGRES_PORT", postgres.Server.GetEndpoint("tcp").Property(EndpointProperty.Port))
            .WithEnvironment("POSTGRES_USER", postgres.User)
            .WithEnvironment("POSTGRES_PASSWORD", postgres.Password)
            .WithEnvironment("POSTGRES_ARCHIVE_DIR", $"{ContainerBackupRoot}/wal")
            .WithEnvironment("OPENBAO_ADDR", openBao.Server.GetEndpoint("http"))
            .WithEnvironment("OPENBAO_TOKEN", openBaoToken)
            .WithEnvironment("OPENBAO_KV_MOUNT", "secret")
            // Ensures Aspire's compose publisher emits FROSTSTREAM_BACKUP_ROOT in .env.
            .WithEnvironment("Backup__HostRoot", backupRootParameter)
            .WaitForOpenBao(openBao)
            .WithPortableBindMount(
                backupRoot,
                "${FROSTSTREAM_BACKUP_ROOT:-./backups}",
                ContainerBackupRoot);

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
