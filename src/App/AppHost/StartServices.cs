namespace AppHost;

public static class StartServices
{
    // Container-side path the shared storage directory is bind-mounted to in publish mode.
    // Run mode runs services directly on the host, so they get the host directory instead;
    // the host path is not absolute inside a Linux container and would fail storage checks.
    private const string ContainerStorageRoot = "/data";

    public static void Wire(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        PostgresResources postgres,
        OpenBaoResources openBaoResources,
        IResourceBuilder<ParameterResource> openBaoToken,
        IResourceBuilder<ContainerResource> typesense,
        IResourceBuilder<ParameterResource> typesenseApiKey,
        AuthentikResources authentik,
        OpenFgaResources openFga,
        IResourceBuilder<ContainerResource> potProvider,
        IResourceBuilder<ContainerResource> backupService,
        ClickHouseResources clickHouse)
    {
        var openBao = openBaoResources.Server;
        var webApiEndpointName = hardening.EnableHttps ? "https" : "http";
        var databridge = WireDataBridge(builder, hardening, sharedStorageRoot, nats, postgres, openBaoResources, openBaoToken, typesense, typesenseApiKey, potProvider, clickHouse);
        var webapi = WireWebApi(builder, hardening, sharedStorageRoot, nats, databridge, openBaoResources, openBaoToken, authentik, openFga, backupService, webApiEndpointName, clickHouse);
        WireWorker(builder, hardening, sharedStorageRoot, nats, openBaoResources, openBaoToken, clickHouse);
        WireMediaProcessor(builder, nats, databridge, webapi, webApiEndpointName);
        WireScheduler(builder, nats, databridge, backupService);
        //WireAuthTester(builder, hardening, webapi, authentik, webApiEndpointName);
        WireFrontend(builder, webapi, webApiEndpointName);
    }

    private static IResourceBuilder<ProjectResource> WireDataBridge(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        PostgresResources postgres,
        OpenBaoResources openBaoResources,
        IResourceBuilder<ParameterResource> openBaoToken,
        IResourceBuilder<ContainerResource> typesense,
        IResourceBuilder<ParameterResource> typesenseApiKey,
        IResourceBuilder<ContainerResource> potProvider,
        ClickHouseResources clickHouse)
    {
        var deployment = DeploymentRuntime.Current;
        var openBao = openBaoResources.Server;
        var storageRoot = builder.ExecutionContext.IsRunMode ? sharedStorageRoot : ContainerStorageRoot;
        IResourceBuilder<ProjectResource>? databridgeInitializer = null;
        if (deployment.Selection.Profile.IncludeInitialization)
        {
            databridgeInitializer = builder.AddProject<Projects.DataBridge>(deployment.Names.DataBridgeInitialize)
                .WithArgs("initialize")
                .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb).WaitForDatabases(postgres)
                .WithEnvironment("Typesense__Url", typesense.GetEndpoint("http"))
                .WithEnvironment("Typesense__ApiKey", typesenseApiKey)
                .WithEnvironment("FROSTSTREAM_STORAGE_ROOT", storageRoot)
                .WithEnvironment("Initialization__RequiredDirectory", storageRoot)
                .WithEnvironment("Initialization__InitProfile", "frostream-full-init")
                .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
                .WaitFor(typesense)
                .WithComposeDependencyCondition(deployment.Names.Typesense, "service_healthy")
                .PublishAsDockerFile(c => c
                    .WithDockerfile(deployment.Paths.AppProjectDirectory("DataBridge"), "Dockerfile")
                    .WithImage(deployment.Images.ApplicationRepository(deployment.Names.DataBridge), "latest")
                    .WithVolume(deployment.Names.Volume("data"), ContainerStorageRoot))
                .WithLocalComposeBuild(deployment.Images.Application(deployment.Names.DataBridge), deployment.Paths.ComposeDockerfile("DataBridge"))
                .PublishAsDockerComposeService((_, service) =>
                {
                    service.Command = ["initialize"];
                    service.Restart = "no";
                });

            if (clickHouse is { Server: { } initClickHouse, HttpEndpoint: { } initClickHouseEndpoint, Password: { } initClickHousePassword })
            {
                databridgeInitializer = databridgeInitializer
                    .WithEnvironment("LiveChat__Enabled", "true")
                    .WithEnvironment("LiveChat__Url", initClickHouseEndpoint)
                    .WithEnvironment("LiveChat__Database", StartClickHouse.Database)
                    .WithEnvironment("LiveChat__User", StartClickHouse.User)
                    .WithEnvironment("LiveChat__Password", initClickHousePassword)
                    .WaitFor(initClickHouse);
            }
        }

        var databridge = builder.AddProject<Projects.DataBridge>(deployment.Names.DataBridge)
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb).WaitForDatabases(postgres)
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", openBaoToken)
            .WithEnvironment("Typesense__Url", typesense.GetEndpoint("http"))
            .WithEnvironment("Typesense__ApiKey", typesenseApiKey)
            .WithEnvironment(ctx => ctx.EnvironmentVariables["FROSTSTREAM_STORAGE_ROOT"] =
                ctx.ExecutionContext.IsRunMode ? sharedStorageRoot : ContainerStorageRoot)
            .WithEnvironment("Initialization__RequiredDirectory", storageRoot)
            .WithEnvironment("Initialization__InitProfile", "frostream-full-init")
            .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            // POT broker role: answers Worker pot.request messages from the co-located bgutil provider.
            .WithEnvironment("PotBroker__Enabled", "true")
            .WithEnvironment("PotBroker__ProviderUrl", potProvider.GetEndpoint("http"))
            .WaitForOpenBao(openBaoResources)
            .WaitFor(typesense)
            .WithComposeDependencyCondition(deployment.Names.Typesense, "service_healthy")
            .WaitFor(potProvider)
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    deployment.Paths.AppProjectDirectory("DataBridge"),
                    "Dockerfile")
                .WithImage(deployment.Images.ApplicationRepository(deployment.Names.DataBridge), "latest")
                // Named volume (shared by databridge/webapi/worker) instead of a host bind mount
                // so the compose export stays machine-portable.
                .WithVolume(deployment.Names.Volume("data"), ContainerStorageRoot))
            .WithLocalComposeBuild(deployment.Images.Application(deployment.Names.DataBridge), deployment.Paths.ComposeDockerfile("DataBridge"));

        if (databridgeInitializer is not null)
            databridge.WaitForCompletion(databridgeInitializer);

        if (clickHouse is { Server: { } clickHouseServer, HttpEndpoint: { } clickHouseEndpoint, Password: { } clickHousePassword })
        {
            databridge = databridge
                .WithEnvironment("LiveChat__Enabled", "true")
                .WithEnvironment("LiveChat__Url", clickHouseEndpoint)
                .WithEnvironment("LiveChat__Database", StartClickHouse.Database)
                .WithEnvironment("LiveChat__User", StartClickHouse.User)
                .WithEnvironment("LiveChat__Password", clickHousePassword)
                .WaitFor(clickHouseServer);
        }

        return databridge
            .WithComposeDependencyCondition(deployment.Names.Postgres, "service_healthy")
            .WithComposeDependencyCondition(deployment.Names.OpenBao, "service_healthy");
    }

    private static IResourceBuilder<ProjectResource> WireWebApi(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ProjectResource> databridge,
        OpenBaoResources openBaoResources,
        IResourceBuilder<ParameterResource> openBaoToken,
        AuthentikResources authentik,
        OpenFgaResources openFga,
        IResourceBuilder<ContainerResource> backupService,
        string webApiEndpointName,
        ClickHouseResources clickHouse)
    {
        var deployment = DeploymentRuntime.Current;
        var openBao = openBaoResources.Server;
        // LAN-reachable base URL that cast devices use to fetch media; deployment-specific, so
        // parameterized to land in the compose .env rather than the yaml.
        var castAdvertisedBaseUrl = builder.AddParameter(
            "cast-advertised-base-url",
            Environment.GetEnvironmentVariable("CAST_ADVERTISED_BASE_URL") ?? "",
            publishValueAsDefault: false);
        var publicOrigin = builder.AddParameter(
            "frontend-public-origin",
            FrontendPublicOrigin(),
            publishValueAsDefault: false);
        var publicAuthority = builder.AddParameter(
            "authentik-public-authority",
            FrontendPublicAuthAuthority(hardening),
            publishValueAsDefault: false);

        var webApiUrls = hardening.EnableHttps
            ? $"http://0.0.0.0:{Ports.WebApiHttp};https://0.0.0.0:{Ports.WebApiHttps}"
            : $"http://0.0.0.0:{Ports.WebApiHttp}";

        var webapi = builder.AddProject<Projects.WebAPI>(deployment.Names.WebApi, launchProfileName: webApiEndpointName)
            .WithReference(nats).WaitFor(nats)
            .WaitFor(databridge)
            // Published ASP.NET images default to Production. Keep the WebAPI runtime environment
            // aligned with the AppHost hardening profile so the local HTTP compose profile remains
            // a development deployment while hardened exports enforce production-only checks.
            .WithEnvironment("DOTNET_ENVIRONMENT", hardening.IsProduction ? "Production" : "Development")
            // Run mode only: in publish mode the container binds via the Dockerfile's
            // HTTP_PORTS=8080, and ASPNETCORE_URLS would override that to the host port, leaving
            // the host:8080 port mapping and the frontend's http://webapi:8080 pointing at a dead port.
            .WithEnvironment(ctx =>
            {
                if (ctx.ExecutionContext.IsRunMode)
                {
                    ctx.EnvironmentVariables["ASPNETCORE_URLS"] = webApiUrls;
                }
            })
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", openBaoToken)
            .WithEnvironment(ctx => ctx.EnvironmentVariables["FROSTSTREAM_STORAGE_ROOT"] =
                ctx.ExecutionContext.IsRunMode ? sharedStorageRoot : ContainerStorageRoot)
            .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("Auth__SingleUserMode", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("Auth__AllowSingleUserModeInProduction", Environment.GetEnvironmentVariable("AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION") ?? "false")
            .WithEnvironment("Auth__Audience", Environment.GetEnvironmentVariable("AUTHENTIK_API_AUDIENCE") ?? "froststream-api")
            .WithEnvironment("Auth__ClientId", authentik.ClientId)
            .WithEnvironment("Auth__ClientSecret", authentik.ClientSecret)
            .WithEnvironment("Auth__Scopes", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups offline_access")
            .WithEnvironment("Auth__PublicOrigin", publicOrigin)
            .WithEnvironment("Auth__PublicAuthority", publicAuthority)
            .WithEnvironment("Auth__SecureCookies", Environment.GetEnvironmentVariable("AUTH_SECURE_COOKIES") ?? (hardening.EnableHttps ? "true" : "false"))
            .WithEnvironment(ctx => ctx.EnvironmentVariables["Auth__DataProtectionKeysPath"] =
                ctx.ExecutionContext.IsRunMode
                    ? Path.Combine(sharedStorageRoot, "data-protection-keys")
                    : "/data-protection-keys")
            .WithEnvironment("Auth__RequireHttpsMetadata", hardening.RequireHttpsMetadata ? "true" : "false")
            .WithEnvironment("Auth__ExposeOpenApi", Environment.GetEnvironmentVariable("AUTH_EXPOSE_OPENAPI") ?? "false")
            .WithEnvironment("OpenFga__StoreId", Environment.GetEnvironmentVariable("OPENFGA_STORE_ID") ?? "")
            .WithEnvironment("OpenFga__AuthorizationModelId", Environment.GetEnvironmentVariable("OPENFGA_AUTHORIZATION_MODEL_ID") ?? "")
            .WithEnvironment("OpenFga__AutoProvision", Environment.GetEnvironmentVariable("OPENFGA_AUTO_PROVISION") ?? "true")
            .WithEnvironment("OpenFga__BootstrapOwnerSubjects", Environment.GetEnvironmentVariable("OPENFGA_BOOTSTRAP_OWNER_SUB") ?? "")
            .WithEnvironment("Cast__AdvertisedBaseUrl", castAdvertisedBaseUrl)
            // WebAPI only needs the flag: chat queries proxy to DataBridge over NATS.
            .WithEnvironment("LiveChat__Enabled", clickHouse.Server is not null ? "true" : "false")
            .WithEnvironment("BackupService__BaseUrl", backupService.GetEndpoint("http"))
            .WaitForOpenBao(openBaoResources)
            .WaitFor(backupService)
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    deployment.Paths.AppProjectDirectory("WebAPI"),
                    "Dockerfile")
                .WithImage(deployment.Images.ApplicationRepository(deployment.Names.WebApi), "latest")
                // Named volume (shared by databridge/webapi/worker) instead of a host bind mount
                // so the compose export stays machine-portable.
                .WithVolume(deployment.Names.Volume("data"), ContainerStorageRoot)
                .WithVolume(deployment.Names.Volume("data-protection-keys"), "/data-protection-keys"))
            .WithLocalComposeBuild(deployment.Images.Application(deployment.Names.WebApi), deployment.Paths.ComposeDockerfile("WebAPI"));

        webapi.WithComposeDependencyCondition(deployment.Names.OpenBao, "service_healthy");
        webapi.WithComposeDependencyCondition(deployment.Names.BackupService, "service_healthy");
        webapi.WithComposeDependencyCondition(deployment.Names.DataBridge, "service_started");
        webapi.WithEndpointProxySupport(false);
        var isPublishMode = builder.ExecutionContext.IsPublishMode;
        webapi.WithEndpoint("http", endpoint =>
        {
            endpoint.TargetHost = "0.0.0.0";
            endpoint.IsProxied = false;
            endpoint.IsExternal = true;
            // Publish would default the host port to the container port (8080); pin it. Run
            // mode gets the same port via ASPNETCORE_URLS/the launch profile.
            if (isPublishMode)
            {
                endpoint.Port = Ports.WebApiHttp;
            }
        }, createIfNotExists: false);
        webapi.WithEndpoint("https", endpoint =>
        {
            endpoint.TargetHost = "0.0.0.0";
            endpoint.IsProxied = false;
            // Without EnableHttps the container serves plain HTTP only, so publishing a host
            // mapping for the https endpoint would just expose a dead port.
            endpoint.IsExternal = hardening.EnableHttps;
            if (isPublishMode && hardening.EnableHttps)
            {
                endpoint.Port = Ports.WebApiHttps;
            }
        }, createIfNotExists: false);

        webapi = webapi.WithAuthAuthority("Auth__Authority", hardening.SingleUserMode, authentik);

        if (!hardening.SingleUserMode && authentik.Server is { } authentikServer && openFga.Server is not null && openFga.Endpoint is not null)
        {
            webapi = webapi
                .WithEnvironment("OpenFga__Endpoint", openFga.Endpoint)
                .WithEnvironment("Authentik__ApiUrl", authentikServer.GetEndpoint("http"))
                .WaitFor(authentikServer)
                .WithComposeDependencyCondition(deployment.Names.Authentik, "service_healthy")
                .WaitFor(openFga.Server);

            if (authentik.ApiToken is { } authentikApiToken)
            {
                webapi = webapi.WithEnvironment("Authentik__ApiToken", authentikApiToken);
            }

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
        OpenBaoResources openBaoResources,
        IResourceBuilder<ParameterResource> openBaoToken,
        ClickHouseResources clickHouse)
    {
        var deployment = DeploymentRuntime.Current;
        var openBao = openBaoResources.Server;
        builder.AddProject<Projects.Worker>(deployment.Names.Worker)
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", openBaoToken)
            .WithEnvironment(ctx => ctx.EnvironmentVariables["FROSTSTREAM_STORAGE_ROOT"] =
                ctx.ExecutionContext.IsRunMode ? sharedStorageRoot : ContainerStorageRoot)
            .WithEnvironment("LiveChat__Enabled", clickHouse.Server is not null ? "true" : "false")
            // Start the loopback HTTP→NATS POT shim and inject the bgutil extractor-args. The Worker
            // reaches a provider via the pot-brokers queue group over NATS, not a direct container URL.
            .WithEnvironment("PotProvider__Enabled", "true")
            .WaitForOpenBao(openBaoResources)
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    deployment.Paths.AppProjectDirectory("Worker"),
                    "Dockerfile")
                .WithImage(deployment.Images.ApplicationRepository(deployment.Names.Worker), "latest")
                // Named volume (shared by databridge/webapi/worker) instead of a host bind mount
                // so the compose export stays machine-portable.
                .WithVolume(deployment.Names.Volume("data"), ContainerStorageRoot))
            .WithLocalComposeBuild(deployment.Images.Application(deployment.Names.Worker), deployment.Paths.ComposeDockerfile("Worker"))
            .WithComposeDependencyCondition(deployment.Names.OpenBao, "service_healthy");
    }

    private static void WireMediaProcessor(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ProjectResource> databridge,
        IResourceBuilder<ProjectResource> webapi,
        string webApiEndpointName)
    {
        var deployment = DeploymentRuntime.Current;
        // Rendition claims/completions still go through DataBridge over NATS. Media bytes move
        // through WebAPI's internal HTTP storage endpoints, so MediaProcessor needs neither a
        // storage mount nor OpenBao. ffmpeg/ffprobe come from the container image (publish) or the
        // host PATH (run mode).
        builder.AddProject<Projects.MediaProcessor>(deployment.Names.MediaProcessor)
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("MediaProcessor__WebApiBaseUrl", webapi.GetEndpoint(webApiEndpointName))
            .WaitFor(databridge)
            .WaitFor(webapi)
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    deployment.Paths.AppProjectDirectory("MediaProcessor"),
                    "Dockerfile")
                .WithImage(deployment.Images.ApplicationRepository(deployment.Names.MediaProcessor), "latest"))
            .WithLocalComposeBuild(deployment.Images.Application(deployment.Names.MediaProcessor), deployment.Paths.ComposeDockerfile("MediaProcessor"))
            .WithComposeDependencyCondition(deployment.Names.DataBridge, "service_started")
            .WithComposeDependencyCondition(deployment.Names.WebApi, "service_started");
    }

    private static void WireScheduler(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ProjectResource> databridge,
        IResourceBuilder<ContainerResource> backupService)
    {
        var deployment = DeploymentRuntime.Current;
        var scheduler = builder.AddProject<Projects.Scheduler>(deployment.Names.Scheduler)
            .WithReference(nats).WaitFor(nats)
            .WaitFor(databridge)
            // Scheduled backups dispatch over REST directly to BackupService.
            .WithEnvironment("BackupService__BaseUrl", backupService.GetEndpoint("http"))
            .WaitFor(backupService)
            .WithHttpEndpoint(port: Ports.Scheduler, name: "http")
            .WithUrlForEndpoint("http", url =>
            {
                url.Url = "/quartz";
                url.DisplayText = "Quartz";
            })
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    deployment.Paths.AppProjectDirectory("Scheduler"),
                    "Dockerfile")
                .WithImage(deployment.Images.ApplicationRepository(deployment.Names.Scheduler), "latest"))
            .WithLocalComposeBuild(deployment.Images.Application(deployment.Names.Scheduler), deployment.Paths.ComposeDockerfile("Scheduler"));

        // The Quartz UI is host-facing, so publish a host mapping. The container itself keeps
        // listening on the aspnet default (HTTP_PORTS=8080).
        scheduler.WithEndpoint("http", endpoint =>
        {
            endpoint.IsExternal = true;
            if (builder.ExecutionContext.IsPublishMode)
            {
                endpoint.Port = Ports.Scheduler;
                endpoint.TargetPort = 8080;
            }
        }, createIfNotExists: false);
    }
    
    
    private static void WireFrontend(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> webapi,
        string webApiEndpointName)
    {
        var deployment = DeploymentRuntime.Current;
        var frontend = builder.AddViteApp(deployment.Names.Frontend, deployment.Paths.AppProjectDirectory("Frontend"))
            .WithPnpm()
            .WithExternalHttpEndpoints()
            .WithReference(webapi)
            .WaitFor(webapi)
            .WithEnvironment("WEBAPI_UPSTREAM", webapi.GetEndpoint(webApiEndpointName))
            .WithLocalComposeBuild(deployment.Images.Application(deployment.Names.Frontend), deployment.Paths.ComposeDockerfile("Frontend"));

        // Pin the host port in both modes; vite proxies during development and Caddy proxies in the
        // published image using the same /api, /auth, and /stream contract.
        frontend.WithEndpoint("http", endpoint => endpoint.Port = Ports.Frontend, createIfNotExists: false);
    }

    private static string FrontendPublicAuthAuthority(AppHostHardeningOptions hardening)
    {
        if (hardening.SingleUserMode)
        {
            return "";
        }

        return Environment.GetEnvironmentVariable("AUTHENTIK_PUBLIC_AUTHORITY")
               ?? Environment.GetEnvironmentVariable("AUTHENTIK_AUTHORITY")
               ?? $"http://localhost:{Ports.Authentik}/application/o/froststream/";
    }

    private static string FrontendPublicOrigin()
        => (Environment.GetEnvironmentVariable("FRONTEND_PUBLIC_ORIGIN") ?? $"http://localhost:{Ports.Frontend}").TrimEnd('/');

    private static IResourceBuilder<TResource> WithLocalComposeBuild<TResource>(
        this IResourceBuilder<TResource> resource,
        string image,
        string dockerfile)
        where TResource : IComputeResource
    {
        return resource.PublishAsDockerComposeService((_, service) =>
        {
            service.Image = image;
            service.PullPolicy = "build";
            service.Build = new Aspire.Hosting.Docker.Resources.ServiceNodes.Build
            {
                // docker-compose.yaml is emitted under src/App/docker-compose-artifacts.
                // The Dockerfiles expect the repository src/ directory as build context.
                Context = DeploymentRuntime.Current.Paths.ComposeBuildContext,
                Dockerfile = dockerfile
            };
        });
    }
}
