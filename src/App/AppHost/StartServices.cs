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
        //WireAuthTester(builder, hardening, webapi, authentik, webApiEndpointName);
        //WireFrontend(builder, hardening, webapi, authentik, webApiEndpointName);
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
        var databridge = builder.AddProject<Projects.DataBridge>("databridge")
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

        // Scheduled backups run in DataBridge, so it needs the same BackupTool wiring as WebAPI.
        return ApplyBackupEnvironment(databridge, builder.AppHostDirectory, sharedStorageRoot, hardening, openBao);
    }

    /// <summary>
    /// Applies the shared BackupTool environment (`Backup__*`) to a service that shells out to the
    /// tool. Both WebAPI (on-demand) and DataBridge (scheduled) use this so their configuration stays
    /// in lockstep, including the WAL archive directory shared with the Postgres container.
    /// </summary>
    private static IResourceBuilder<ProjectResource> ApplyBackupEnvironment(
        IResourceBuilder<ProjectResource> resource,
        string appHostDirectory,
        string sharedStorageRoot,
        AppHostHardeningOptions hardening,
        IResourceBuilder<ContainerResource> openBao)
    {
        var backupToolProject = Path.GetFullPath(Path.Combine(
            appHostDirectory,
            "..",
            "BackupTool",
            "BackupTool.csproj"));

        return resource
            .WithEnvironment("Backup__Directory", BackupPaths.BackupRoot(sharedStorageRoot))
            .WithEnvironment("Backup__ToolPath", "dotnet")
            .WithEnvironment("Backup__ToolBaseArguments", $"run --project {backupToolProject} --")
            .WithEnvironment("Backup__ArchiveDir", BackupPaths.WalArchiveDirectory(sharedStorageRoot))
            .WithEnvironment("Backup__OpenBaoAddress", openBao.GetEndpoint("http"))
            .WithEnvironment("Backup__OpenBaoToken", hardening.OpenBaoToken)
            .WithEnvironment("Backup__OpenBaoKvMount", "secret");
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
        var webApiUrls = hardening.EnableHttps
            ? "http://0.0.0.0:5041;https://0.0.0.0:7035"
            : "http://0.0.0.0:5041";

        var webapi = builder.AddProject<Projects.WebAPI>("webapi", launchProfileName: webApiEndpointName)
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb)
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("ASPNETCORE_URLS", webApiUrls)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", hardening.OpenBaoToken)
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
            // LAN-reachable base URL that cast devices use to fetch media. Required for real-device
            // casting under Aspire, whose proxied endpoints bind to localhost.
            .WithEnvironment("Cast__AdvertisedBaseUrl", Environment.GetEnvironmentVariable("CAST_ADVERTISED_BASE_URL") ?? "")
            .WaitFor(openBao);

        webapi = ApplyBackupEnvironment(webapi, builder.AppHostDirectory, sharedStorageRoot, hardening, openBao);

        webapi.WithEndpointProxySupport(false);
        webapi.WithEndpoint("http", endpoint =>
        {
            endpoint.TargetHost = "0.0.0.0";
            endpoint.IsProxied = false;
        }, createIfNotExists: false);
        webapi.WithEndpoint("https", endpoint =>
        {
            endpoint.TargetHost = "0.0.0.0";
            endpoint.IsProxied = false;
        }, createIfNotExists: false);

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

    // private static void WireAuthTester(
    //     IDistributedApplicationBuilder builder,
    //     AppHostHardeningOptions hardening,
    //     IResourceBuilder<ProjectResource> webapi,
    //     AuthentikResources authentik,
    //     string webApiEndpointName)
    // {
    //     var authTester = builder.AddViteApp("auth-tester", "../../HelperTestingApps/AuthTester")
    //         .WithPnpm()
    //         .WithReference(webapi)
    //         .WaitFor(webapi)
    //         .WithEnvironment("VITE_API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
    //         .WithEnvironment("API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
    //         .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
    //         .WithEnvironment("VITE_SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
    //         .WithEnvironment("VITE_AUTH_MODE", hardening.SingleUserMode ? "single-user" : "multi-user")
    //         .WithEnvironment("AUTH_CLIENT_ID", authentik.ClientId)
    //         .WithEnvironment("AUTH_CLIENT_SECRET", authentik.ClientSecret)
    //         .WithEnvironment("AUTH_SCOPES", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups offline_access");
    //
    //     authTester = authTester.WithAuthAuthority("VITE_AUTH_AUTHORITY", hardening.SingleUserMode, authentik);
    //     authTester = authTester.WithAuthAuthority("AUTH_AUTHORITY", hardening.SingleUserMode, authentik);
    //
    //     if (!hardening.SingleUserMode && authentik.Server is { } authentikServer)
    //     {
    //         authTester = authTester.WaitFor(authentikServer);
    //     }
    // }
    
    // private static void WireFrontend(
    //     IDistributedApplicationBuilder builder,
    //     AppHostHardeningOptions hardening,
    //     IResourceBuilder<ProjectResource> webapi,
    //     AuthentikResources authentik,
    //     string webApiEndpointName)
    // {
    //     var frontend = builder.AddViteApp("frontend", "../Frontend")
    //         .WithPnpm()
    //         .WithReference(webapi)
    //         .WaitFor(webapi)
    //         .WithEnvironment("VITE_API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
    //         .WithEnvironment("API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
    //         .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
    //         .WithEnvironment("VITE_SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
    //         .WithEnvironment("VITE_AUTH_MODE", hardening.SingleUserMode ? "single-user" : "multi-user")
    //         .WithEnvironment("AUTH_CLIENT_ID", authentik.ClientId)
    //         .WithEnvironment("AUTH_CLIENT_SECRET", authentik.ClientSecret)
    //         .WithEnvironment("AUTH_SCOPES", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups offline_access");
    //
    //     frontend = frontend.WithAuthAuthority("VITE_AUTH_AUTHORITY", hardening.SingleUserMode, authentik);
    //     frontend = frontend.WithAuthAuthority("AUTH_AUTHORITY", hardening.SingleUserMode, authentik);
    //
    //     if (!hardening.SingleUserMode && authentik.Server is { } authentikServer)
    //     {
    //         frontend = frontend.WaitFor(authentikServer);
    //     }
    // }
}
