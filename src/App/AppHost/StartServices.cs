namespace AppHost;

public static class StartServices
{
    public static void Wire(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        PostgresResources postgres,
        IResourceBuilder<ContainerResource> openBao,
        IResourceBuilder<ContainerResource> typesense,
        AuthentikResources authentik,
        OpenFgaResources openFga,
        IResourceBuilder<ContainerResource> potProvider)
    {
        var webApiEndpointName = hardening.EnableHttps ? "https" : "http";

        var databridge = WireDataBridge(builder, hardening, sharedStorageRoot, nats, postgres, openBao, typesense, potProvider);
        var webapi = WireWebApi(builder, hardening, sharedStorageRoot, nats, postgres, openBao, authentik, openFga, webApiEndpointName);
        WireWorker(builder, hardening, sharedStorageRoot, nats, openBao);
        WireScheduler(builder, nats, databridge);
        WireAuthTester(builder, hardening, webapi, authentik, webApiEndpointName);
    }

    private static IResourceBuilder<ProjectResource> WireDataBridge(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        PostgresResources postgres,
        IResourceBuilder<ContainerResource> openBao,
        IResourceBuilder<ContainerResource> typesense,
        IResourceBuilder<ContainerResource> potProvider)
    {
        return builder.AddProject<Projects.DataBridge>("databridge")
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb)
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", hardening.OpenBaoToken)
            .WithEnvironment("Typesense__Url", typesense.GetEndpoint("http"))
            .WithEnvironment("Typesense__ApiKey", hardening.TypesenseApiKey)
            .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
            .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            // POT broker role: answers Worker pot.request messages from the co-located bgutil provider.
            .WithEnvironment("PotBroker__Enabled", "true")
            .WithEnvironment("PotBroker__ProviderUrl", potProvider.GetEndpoint("http"))
            .WaitFor(openBao)
            .WaitFor(typesense)
            .WaitFor(potProvider);
    }

    private static IResourceBuilder<ProjectResource> WireWebApi(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        PostgresResources postgres,
        IResourceBuilder<ContainerResource> openBao,
        AuthentikResources authentik,
        OpenFgaResources openFga,
        string webApiEndpointName)
    {
        var backupToolProject = Path.GetFullPath(Path.Combine(
            builder.AppHostDirectory,
            "..",
            "BackupTool",
            "BackupTool.csproj"));
        var backupRoot = Environment.GetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT")
                         ?? Path.Combine(sharedStorageRoot, "core-backups");

        var webapi = builder.AddProject<Projects.WebAPI>("webapi", launchProfileName: webApiEndpointName)
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb)
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", hardening.OpenBaoToken)
            .WithEnvironment("Backup__Directory", backupRoot)
            .WithEnvironment("Backup__ToolPath", "dotnet")
            .WithEnvironment("Backup__ToolBaseArguments", $"run --project {backupToolProject} --")
            .WithEnvironment("Backup__OpenBaoAddress", openBao.GetEndpoint("http"))
            .WithEnvironment("Backup__OpenBaoToken", hardening.OpenBaoToken)
            .WithEnvironment("Backup__OpenBaoKvMount", "secret")
            .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
            .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("Auth__SingleUserMode", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("Auth__AllowSingleUserModeInProduction", Environment.GetEnvironmentVariable("AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION") ?? "false")
            .WithEnvironment("Auth__Audience", Environment.GetEnvironmentVariable("AUTHENTIK_API_AUDIENCE") ?? "froststream-api")
            .WithEnvironment("Auth__ClientId", authentik.ClientId)
            .WithEnvironment("Auth__RequireHttpsMetadata", hardening.RequireHttpsMetadata ? "true" : "false")
            .WithEnvironment("Auth__ExposeOpenApi", Environment.GetEnvironmentVariable("AUTH_EXPOSE_OPENAPI") ?? "false")
            .WithEnvironment("OpenFga__StoreId", Environment.GetEnvironmentVariable("OPENFGA_STORE_ID") ?? "")
            .WithEnvironment("OpenFga__AuthorizationModelId", Environment.GetEnvironmentVariable("OPENFGA_AUTHORIZATION_MODEL_ID") ?? "")
            .WithEnvironment("OpenFga__AutoProvision", Environment.GetEnvironmentVariable("OPENFGA_AUTO_PROVISION") ?? "true")
            .WithEnvironment("OpenFga__BootstrapOwnerSubjects", Environment.GetEnvironmentVariable("OPENFGA_BOOTSTRAP_OWNER_SUB") ?? "")
            .WaitFor(openBao);

        webapi = webapi.WithAuthAuthority("Auth__Authority", hardening.SingleUserMode, authentik);

        if (!hardening.SingleUserMode && authentik.Server is { } authentikServer && openFga.Server is not null && openFga.Endpoint is not null)
        {
            webapi = webapi
                .WithEnvironment("OpenFga__Endpoint", openFga.Endpoint)
                .WaitFor(authentikServer)
                .WaitFor(openFga.Server);

            if (hardening.EnableFgaAuthenticatedEndpoints)
            {
                webapi = webapi.WithEnvironment("OpenFga__ApiToken", hardening.OpenFgaApiToken);
            }
        }

        return webapi;
    }

    private static void WireWorker(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ContainerResource> openBao)
    {
        builder.AddProject<Projects.Worker>("worker")
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", hardening.OpenBaoToken)
            .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", sharedStorageRoot)
            // Start the loopback HTTP→NATS POT shim and inject the bgutil extractor-args. The Worker
            // reaches a provider via the pot-brokers queue group over NATS, not a direct container URL.
            .WithEnvironment("PotProvider__Enabled", "true")
            .WaitFor(openBao);
    }

    private static void WireScheduler(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ProjectResource> databridge)
    {
        builder.AddProject<Projects.Scheduler>("scheduler")
            .WithReference(nats).WaitFor(nats)
            .WaitFor(databridge)
            .WithHttpEndpoint(name: "http")
            .WithUrlForEndpoint("http", url =>
            {
                url.Url = "/quartz";
                url.DisplayText = "Quartz";
            });
    }

    private static void WireAuthTester(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        IResourceBuilder<ProjectResource> webapi,
        AuthentikResources authentik,
        string webApiEndpointName)
    {
        var authTester = builder.AddViteApp("auth-tester", "../../HelperTestingApps/AuthTester")
            .WithPnpm()
            .WithReference(webapi)
            .WaitFor(webapi)
            .WithEnvironment("VITE_API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
            .WithEnvironment("API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
            .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("VITE_SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("VITE_AUTH_MODE", hardening.SingleUserMode ? "single-user" : "multi-user")
            .WithEnvironment("AUTH_CLIENT_ID", authentik.ClientId)
            .WithEnvironment("AUTH_CLIENT_SECRET", authentik.ClientSecret)
            .WithEnvironment("AUTH_SCOPES", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups");

        authTester = authTester.WithAuthAuthority("VITE_AUTH_AUTHORITY", hardening.SingleUserMode, authentik);
        authTester = authTester.WithAuthAuthority("AUTH_AUTHORITY", hardening.SingleUserMode, authentik);

        if (!hardening.SingleUserMode && authentik.Server is { } authentikServer)
        {
            authTester = authTester.WaitFor(authentikServer);
        }
    }
}
