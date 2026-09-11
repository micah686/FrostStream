using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FrostStream.Deployment;

public sealed record ContainerImage(string Repository, string Tag)
{
    public string FullName => $"{Repository}:{Tag}";
}

public sealed record DeploymentNames(string InstallationName)
{
    public string Network => $"{InstallationName}-network";
    public string Volume(string purpose)
    {
        if (InstallationName != DeploymentProfiles.Full.InstallationName)
        {
            return $"{InstallationName}-{purpose}";
        }

        return purpose switch
        {
            "postgres-data" => "froststream-postgres-data",
            "postgres-socket" => "froststream-postgres-socket",
            "data" => "froststream-data",
            "data-protection-keys" => "froststream-data-protection-keys",
            "openbao-data" => "openbao-data",
            "typesense-data" => "typesense-data",
            "clickhouse-data" => "clickhouse-data",
            _ => $"froststream-{purpose}"
        };
    }

    public string Nats => "nats";
    public string NatsUi => "nats-ui";
    public string Postgres => "postgres";
    public string PostgresInit => "postgres-init";
    public string BackupInit => "backup-init";
    public string DbGate => "dbgate";
    public string OpenBao => "openbao";
    public string OpenBaoBootstrap => "openbao-bootstrap";
    public string Typesense => "typesense";
    public string Authentik => "authentik";
    public string AuthentikWorker => "authentik-worker";
    public string OpenFga => "openfga";
    public string OpenFgaMigrate => "openfga-migrate";
    public string OpenFgaStudio => "openfga-studio";
    public string PotProvider => "pot-provider";
    public string BackupService => "backupservice";
    public string BackupInitialize => "backupservice-initialize";
    public string ClickHouse => "clickhouse";
    public string DataBridge => "databridge";
    public string DataBridgeInitialize => "databridge-initialize";
    public string WebApi => "webapi";
    public string Worker => "worker";
    public string MediaProcessor => "mediaprocessor";
    public string Scheduler => "scheduler";
    public string Frontend => "frontend";
    public string FrostStreamDatabase => "froststreamdb";
    public string AuthentikDatabase => "authentikdb";
    public string OpenFgaDatabase => "openfgadb";
}

public sealed record DeploymentPorts(
    int Frontend,
    int Authentik,
    int WebApiHttp,
    int WebApiHttps,
    int Scheduler,
    int OpenBao,
    int Postgres,
    int DbGate,
    int NatsUi,
    int OpenFgaStudio,
    int BackupRestoreUi,
    int Typesense = 24010,
    int PotProvider = 24020,
    int OpenFga = 24030,
    int NatsClient = 24040,
    int NatsMonitor = 24041,
    int NatsWebSocket = 24042,
    int BackupService = 24050,
    int ClickHouse = 24060);

public sealed record DeploymentImages(
    ContainerImage Postgres,
    ContainerImage DbGate,
    ContainerImage OpenBao,
    ContainerImage Typesense,
    ContainerImage Authentik,
    ContainerImage OpenFga,
    ContainerImage OpenFgaStudio,
    ContainerImage PotProvider,
    ContainerImage ClickHouse,
    ContainerImage NatsUi,
    string LocalRegistry)
{
    public string ApplicationRepository(string component) => $"{LocalRegistry}/froststream-{component}";
    public string Application(string component) => $"{ApplicationRepository(component)}:latest";
}

public sealed record DeploymentPaths(
    string AppHostDirectory,
    string SourceRoot,
    string StorageRoot,
    string BackupRoot,
    string OpenBaoBootstrapRoot,
    string ResolvedRoot,
    string ResolvedEnvironmentFile,
    string PublishOutputDirectory,
    string ComposeBuildContext)
{
    public string AppProjectDirectory(string project) => Path.Combine(SourceRoot, "App", project);
    public string AppHostConfig(params string[] parts) =>
        Path.Combine([AppHostDirectory, "configs", .. parts]);
    public string ComposeDockerfile(string project) => $"App/{project}/Dockerfile";
}

public sealed record DeploymentConfiguration(
    DeploymentSelection Selection,
    DeploymentNames Names,
    DeploymentPorts Ports,
    DeploymentImages Images,
    DeploymentPaths Paths,
    IReadOnlyDictionary<string, string> Values)
{
    public string Get(string name) => Values.TryGetValue(name, out var value) ? value : string.Empty;
}

public sealed record DeploymentConfigurationRequest(
    DeploymentSelection Selection,
    string AppHostDirectory,
    bool IsRunMode,
    IReadOnlyDictionary<string, string> ProcessValues,
    IReadOnlyDictionary<string, string> SelectedFileValues,
    IReadOnlyDictionary<string, string> PreservedValues,
    string? RequestedOutputPath = null,
    Func<string>? SecretGenerator = null);

public static class DeploymentConfigurationResolver
{
    public const string InstallationNameVariable = "FROSTSTREAM_INSTALLATION_NAME";
    public const string DeploymentRootVariable = "FROSTSTREAM_DEPLOYMENT_ROOT";
    public const string PublishOutputVariable = "FROSTSTREAM_PUBLISH_OUTPUT";

    private static readonly string[] SecretNames =
    [
        "POSTGRES_PASSWORD",
        "OPENBAO_TOKEN",
        "TYPESENSE_API_KEY",
        "CLICKHOUSE_PASSWORD",
        "NATS_PASSWORD",
        "NATS_UI_ADMIN_PASS",
        "NATS_UI_JWT_SECRET",
        "AUTHENTIK_CLIENT_SECRET",
        "AUTHENTIK_SECRET_KEY",
        "AUTHENTIK_BOOTSTRAP_PASSWORD",
        "AUTHENTIK_BOOTSTRAP_TOKEN",
        "OPENFGA_API_TOKEN",
        "BACKUP_RESTORE_UI_TOKEN"
    ];

    public static IReadOnlySet<string> SecretInputNames { get; } =
        new HashSet<string>(SecretNames, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> PersistableValues(
        IReadOnlyDictionary<string, string> values)
    {
        var transientNames = new HashSet<string>(StringComparer.Ordinal)
        {
            DeploymentProfiles.ProfileEnvironmentVariable,
            DeploymentProfiles.DeveloperToolsEnvironmentVariable,
            PublishOutputVariable
        };
        return new ReadOnlyDictionary<string, string>(values
            .Where(pair => !transientNames.Contains(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));
    }

    public static IReadOnlySet<string> InputNames { get; } = new HashSet<string>(
        Defaults(DeploymentEdition.Full).Keys
            .Concat(SecretNames)
            .Concat([
                InstallationNameVariable,
                DeploymentRootVariable,
                PublishOutputVariable,
                DeploymentProfiles.ProfileEnvironmentVariable,
                DeploymentProfiles.DeveloperToolsEnvironmentVariable,
                "FROSTSTREAM_STORAGE_ROOT",
                "FROSTSTREAM_BACKUP_ROOT",
                "FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT",
                "AUTH_REQUIRE_HTTPS_METADATA",
                "ENABLE_FGA_AUTHENTICATED_ENDPOINTS",
                "AUTHENTIK_AUTHORITY",
                "AUTHENTIK_PUBLIC_AUTHORITY",
                "AUTHENTIK_SIGNING_KEY_NAME",
                "AUTH_SECURE_COOKIES",
                "OPENFGA_STORE_ID",
                "OPENFGA_AUTHORIZATION_MODEL_ID",
                "OPENFGA_BOOTSTRAP_OWNER_SUB",
                "CAST_ADVERTISED_BASE_URL",
                "FRONTEND_PUBLIC_ORIGIN",
                "NATS_UI_ADMIN_USER"
            ]),
        StringComparer.Ordinal);

    public static DeploymentConfiguration Resolve(DeploymentConfigurationRequest request)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        Merge(merged, Defaults(request.Selection.Profile.Edition));
        Merge(merged, request.PreservedValues);
        Merge(merged, request.SelectedFileValues, ignoreEmpty: true);
        Merge(merged, request.ProcessValues, ignoreEmpty: true);

        var installationName = Read(merged, InstallationNameVariable) ?? request.Selection.Profile.InstallationName;
        ValidateInstallationName(installationName, request.Selection.Profile.Edition);
        merged[InstallationNameVariable] = installationName;
        merged[DeploymentProfiles.ProfileEnvironmentVariable] = request.Selection.Profile.Name;
        merged[DeploymentProfiles.DeveloperToolsEnvironmentVariable] =
            request.Selection.IncludeDeveloperTools ? "true" : "false";

        var generateSecret = request.SecretGenerator ?? GenerateSecret;
        foreach (var secretName in SecretNames)
        {
            if (!merged.TryGetValue(secretName, out var value) || string.IsNullOrWhiteSpace(value))
            {
                merged[secretName] = generateSecret();
            }
        }

        var appHostDirectory = FullPath(request.AppHostDirectory, Directory.GetCurrentDirectory());
        var sourceRoot = FullPath(Path.Combine(appHostDirectory, "..", ".."), appHostDirectory);
        var repositoryRoot = FullPath(Path.Combine(sourceRoot, ".."), sourceRoot);
        var defaultStorageRoot = installationName == DeploymentProfiles.Full.InstallationName
            ? Path.Combine(repositoryRoot, "data")
            : Path.Combine(repositoryRoot, "data", installationName);
        var storageRoot = FullPath(
            Read(merged, "FROSTSTREAM_STORAGE_ROOT") ?? defaultStorageRoot,
            repositoryRoot);
        var backupRoot = FullPath(
            Read(merged, "FROSTSTREAM_BACKUP_ROOT") ?? Path.Combine(storageRoot, "core-backups"),
            repositoryRoot);
        var openBaoBootstrapRoot = FullPath(
            Read(merged, "FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT") ?? Path.Combine(storageRoot, "openbao-bootstrap"),
            repositoryRoot);
        var resolvedRoot = FullPath(
            Read(merged, DeploymentRootVariable) ?? Path.Combine(appHostDirectory, "deployment", "resolved"),
            repositoryRoot);
        var output = FullPath(
            request.RequestedOutputPath ?? Read(merged, PublishOutputVariable) ??
            Path.Combine(appHostDirectory, "..", "docker-compose-artifacts", installationName, request.Selection.Profile.Name),
            repositoryRoot);
        var composeBuildContext = NormalizeComposePath(Path.GetRelativePath(output, sourceRoot));

        merged["FROSTSTREAM_STORAGE_ROOT"] = storageRoot;
        merged["FROSTSTREAM_BACKUP_ROOT"] = backupRoot;
        merged["FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT"] = openBaoBootstrapRoot;
        merged[DeploymentRootVariable] = resolvedRoot;
        merged[PublishOutputVariable] = output;

        var paths = new DeploymentPaths(
            appHostDirectory,
            sourceRoot,
            storageRoot,
            backupRoot,
            openBaoBootstrapRoot,
            resolvedRoot,
            Path.Combine(resolvedRoot, installationName, "resolved.env"),
            output,
            composeBuildContext);

        return new DeploymentConfiguration(
            request.Selection,
            new DeploymentNames(installationName),
            ReadPorts(merged, request.Selection.Profile.Edition),
            ReadImages(merged),
            paths,
            new ReadOnlyDictionary<string, string>(merged));
    }

    private static Dictionary<string, string> Defaults(DeploymentEdition edition) => new(StringComparer.Ordinal)
    {
        ["POSTGRES_USER"] = "postgres",
        ["TYPESENSE_DATA_DIR"] = "/data",
        ["TYPESENSE_ENABLE_CORS"] = "true",
        ["LIVE_CHAT_ENABLED"] = edition == DeploymentEdition.Lite ? "true" : "false",
        ["SINGLE_USER_MODE"] = edition == DeploymentEdition.Lite ? "true" : "false",
        ["FROSTSTREAM_PRODUCTION"] = "false",
        ["FROSTSTREAM_DEV_TOOLS"] = "false",
        ["ENABLE_HTTPS"] = "false",
        ["AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION"] = "false",
        ["OPENFGA_AUTO_PROVISION"] = "true",
        ["AUTHENTIK_CLIENT_ID"] = "froststream-bff",
        ["AUTHENTIK_BOOTSTRAP_EMAIL"] = "admin@localhost",
        ["AUTHENTIK_API_AUDIENCE"] = "froststream-api",
        ["AUTH_SCOPES"] = "openid profile email groups offline_access",
        ["AUTH_EXPOSE_OPENAPI"] = "false",
        ["NATS_UI_ADMIN_USER"] = "admin",
        ["OPENBAO_IMAGE_TAG"] = "2.5.5",
        ["OPENFGA_IMAGE_TAG"] = "v1.18.0",
        ["OPENFGA_STUDIO_IMAGE_TAG"] = "latest",
        ["BGUTIL_IMAGE_TAG"] = "1.3.1",
        ["TYPESENSE_IMAGE_TAG"] = "30.2",
        ["AUTHENTIK_IMAGE_TAG"] = "2026.5.3",
        ["CLICKHOUSE_IMAGE_TAG"] = "25.8",
        ["LOCAL_IMAGE_REGISTRY"] = "localhost",
        ["PORT_FRONTEND"] = "25000",
        ["PORT_AUTHENTIK"] = "25100",
        ["PORT_WEBAPI_HTTP"] = "25200",
        ["PORT_WEBAPI_HTTPS"] = "25210",
        ["PORT_SCHEDULER"] = "25300",
        ["PORT_OPENBAO"] = "25400",
        ["PORT_POSTGRES"] = "25500",
        ["PORT_DBGATE"] = "25600",
        ["PORT_NATS_UI"] = "25700",
        ["PORT_OPENFGA_STUDIO"] = "25800",
        ["PORT_BACKUP_RESTORE_UI"] = "25900"
    };

    private static DeploymentPorts ReadPorts(IReadOnlyDictionary<string, string> values, DeploymentEdition edition) => new(
        Port(values, "PORT_FRONTEND", edition),
        Port(values, "PORT_AUTHENTIK", edition),
        Port(values, "PORT_WEBAPI_HTTP", edition),
        Port(values, "PORT_WEBAPI_HTTPS", edition),
        Port(values, "PORT_SCHEDULER", edition),
        Port(values, "PORT_OPENBAO", edition),
        Port(values, "PORT_POSTGRES", edition),
        Port(values, "PORT_DBGATE", edition),
        Port(values, "PORT_NATS_UI", edition),
        Port(values, "PORT_OPENFGA_STUDIO", edition),
        Port(values, "PORT_BACKUP_RESTORE_UI", edition));

    private static DeploymentImages ReadImages(IReadOnlyDictionary<string, string> values) => new(
        new("docker.io/library/postgres", "18.3"),
        new("docker.io/dbgate/dbgate", "6.1.4"),
        new("openbao/openbao", values["OPENBAO_IMAGE_TAG"]),
        new("typesense/typesense", values["TYPESENSE_IMAGE_TAG"]),
        new("ghcr.io/goauthentik/server", values["AUTHENTIK_IMAGE_TAG"]),
        new("openfga/openfga", values["OPENFGA_IMAGE_TAG"]),
        new("ghcr.io/prakashm88/openfga-studio", values["OPENFGA_STUDIO_IMAGE_TAG"]),
        new("brainicism/bgutil-ytdlp-pot-provider", values["BGUTIL_IMAGE_TAG"]),
        new("clickhouse/clickhouse-server", values["CLICKHOUSE_IMAGE_TAG"]),
        new("klinux/nats-ui", "0.4.0"),
        values["LOCAL_IMAGE_REGISTRY"].TrimEnd('/'));

    private static int Port(IReadOnlyDictionary<string, string> values, string name, DeploymentEdition edition)
    {
        var value = values[name];
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Invalid {edition} deployment configuration: {name} must be a port from 1 through 65535.");
        }

        return port;
    }

    private static void ValidateInstallationName(string value, DeploymentEdition edition)
    {
        if (!Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                $"Invalid {edition} deployment configuration: {InstallationNameVariable} must contain 1-63 lowercase letters, numbers, or hyphens.");
        }
    }

    private static string? Read(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static void Merge(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string> source,
        bool ignoreEmpty = false)
    {
        foreach (var pair in source)
        {
            if (ignoreEmpty && string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            target[pair.Key] = pair.Value;
        }
    }

    private static string FullPath(string value, string relativeTo) => Path.GetFullPath(
        Path.IsPathRooted(value) ? value : Path.Combine(relativeTo, value));

    private static string NormalizeComposePath(string value) => value.Replace('\\', '/');

    private static string GenerateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
