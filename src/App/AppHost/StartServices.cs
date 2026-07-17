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
        IResourceBuilder<ContainerResource> backupService)
    {
        var openBao = openBaoResources.Server;
        var webApiEndpointName = hardening.EnableHttps ? "https" : "http";

        var databridge = WireDataBridge(builder, hardening, sharedStorageRoot, nats, postgres, openBaoResources, openBaoToken, typesense, typesenseApiKey, potProvider);
        var webapi = WireWebApi(builder, hardening, sharedStorageRoot, nats, postgres, openBaoResources, openBaoToken, authentik, openFga, backupService, webApiEndpointName);
        WireWorker(builder, hardening, sharedStorageRoot, nats, openBaoResources, openBaoToken);
        WireScheduler(builder, nats, databridge);
        //WireAuthTester(builder, hardening, webapi, authentik, webApiEndpointName);
        WireFrontend(builder, hardening, webapi, authentik, webApiEndpointName);
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
        IResourceBuilder<ContainerResource> potProvider)
    {
        var openBao = openBaoResources.Server;
        var databridge = builder.AddProject<Projects.DataBridge>("databridge")
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb).WaitForDatabases(postgres)
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", openBaoToken)
            .WithEnvironment("Typesense__Url", typesense.GetEndpoint("http"))
            .WithEnvironment("Typesense__ApiKey", typesenseApiKey)
            .WithEnvironment(ctx => ctx.EnvironmentVariables["FROSTSTREAM_STORAGE_ROOT"] =
                ctx.ExecutionContext.IsRunMode ? sharedStorageRoot : ContainerStorageRoot)
            .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            // POT broker role: answers Worker pot.request messages from the co-located bgutil provider.
            .WithEnvironment("PotBroker__Enabled", "true")
            .WithEnvironment("PotBroker__ProviderUrl", potProvider.GetEndpoint("http"))
            .WaitForOpenBao(openBaoResources)
            .WaitFor(typesense)
            .WaitFor(potProvider)
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "DataBridge")),
                    "Dockerfile")
                .WithImage("localhost/froststream-databridge", "latest")
                // Named volume (shared by databridge/webapi/worker) instead of a host bind mount
                // so the compose export stays machine-portable.
                .WithVolume("froststream-data", ContainerStorageRoot))
            .WithLocalComposeBuild("localhost/froststream-databridge:latest", "App/DataBridge/Dockerfile");

        return databridge.WithComposeDependencyCondition("openbao", "service_healthy");
    }

    private static IResourceBuilder<ProjectResource> WireWebApi(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening,
        string sharedStorageRoot,
        IResourceBuilder<NatsServerResource> nats,
        PostgresResources postgres,
        OpenBaoResources openBaoResources,
        IResourceBuilder<ParameterResource> openBaoToken,
        AuthentikResources authentik,
        OpenFgaResources openFga,
        IResourceBuilder<ContainerResource> backupService,
        string webApiEndpointName)
    {
        var openBao = openBaoResources.Server;
        // LAN-reachable base URL that cast devices use to fetch media; deployment-specific, so
        // parameterized to land in the compose .env rather than the yaml.
        var castAdvertisedBaseUrl = builder.AddParameter(
            "cast-advertised-base-url",
            Environment.GetEnvironmentVariable("CAST_ADVERTISED_BASE_URL") ?? "",
            publishValueAsDefault: false);

        var webApiUrls = hardening.EnableHttps
            ? $"http://0.0.0.0:{Ports.WebApiHttp};https://0.0.0.0:{Ports.WebApiHttps}"
            : $"http://0.0.0.0:{Ports.WebApiHttp}";

        var webapi = builder.AddProject<Projects.WebAPI>("webapi", launchProfileName: webApiEndpointName)
            .WithReference(postgres.FrostStreamDb).WaitFor(postgres.FrostStreamDb).WaitForDatabases(postgres)
            .WithReference(nats).WaitFor(nats)
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
            .WithEnvironment("Auth__RequireHttpsMetadata", hardening.RequireHttpsMetadata ? "true" : "false")
            .WithEnvironment("Auth__ExposeOpenApi", Environment.GetEnvironmentVariable("AUTH_EXPOSE_OPENAPI") ?? "false")
            .WithEnvironment("OpenFga__StoreId", Environment.GetEnvironmentVariable("OPENFGA_STORE_ID") ?? "")
            .WithEnvironment("OpenFga__AuthorizationModelId", Environment.GetEnvironmentVariable("OPENFGA_AUTHORIZATION_MODEL_ID") ?? "")
            .WithEnvironment("OpenFga__AutoProvision", Environment.GetEnvironmentVariable("OPENFGA_AUTO_PROVISION") ?? "true")
            .WithEnvironment("OpenFga__BootstrapOwnerSubjects", Environment.GetEnvironmentVariable("OPENFGA_BOOTSTRAP_OWNER_SUB") ?? "")
            .WithEnvironment("Cast__AdvertisedBaseUrl", castAdvertisedBaseUrl)
            .WithEnvironment("BackupService__BaseUrl", backupService.GetEndpoint("http"))
            .WaitForOpenBao(openBaoResources)
            .WaitFor(backupService)
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "WebAPI")),
                    "Dockerfile")
                .WithImage("localhost/froststream-webapi", "latest")
                // Named volume (shared by databridge/webapi/worker) instead of a host bind mount
                // so the compose export stays machine-portable.
                .WithVolume("froststream-data", ContainerStorageRoot))
            .WithLocalComposeBuild("localhost/froststream-webapi:latest", "App/WebAPI/Dockerfile");

        webapi.WithComposeDependencyCondition("openbao", "service_healthy");
        webapi.WithComposeDependencyCondition("backupservice", "service_healthy");
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
                .WithComposeDependencyCondition("authentik", "service_healthy")
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
        IResourceBuilder<ParameterResource> openBaoToken)
    {
        var openBao = openBaoResources.Server;
        builder.AddProject<Projects.Worker>("worker")
            .WithReference(nats).WaitFor(nats)
            .WithEnvironment("OpenBao__Address", openBao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__Token", openBaoToken)
            .WithEnvironment(ctx => ctx.EnvironmentVariables["FROSTSTREAM_STORAGE_ROOT"] =
                ctx.ExecutionContext.IsRunMode ? sharedStorageRoot : ContainerStorageRoot)
            // Start the loopback HTTP→NATS POT shim and inject the bgutil extractor-args. The Worker
            // reaches a provider via the pot-brokers queue group over NATS, not a direct container URL.
            .WithEnvironment("PotProvider__Enabled", "true")
            .WaitForOpenBao(openBaoResources)
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "Worker")),
                    "Dockerfile")
                .WithImage("localhost/froststream-worker", "latest")
                // Named volume (shared by databridge/webapi/worker) instead of a host bind mount
                // so the compose export stays machine-portable.
                .WithVolume("froststream-data", ContainerStorageRoot))
            .WithLocalComposeBuild("localhost/froststream-worker:latest", "App/Worker/Dockerfile")
            .WithComposeDependencyCondition("openbao", "service_healthy");
    }

    private static void WireScheduler(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<NatsServerResource> nats,
        IResourceBuilder<ProjectResource> databridge)
    {
        var scheduler = builder.AddProject<Projects.Scheduler>("scheduler")
            .WithReference(nats).WaitFor(nats)
            .WaitFor(databridge)
            .WithHttpEndpoint(port: Ports.Scheduler, name: "http")
            .WithUrlForEndpoint("http", url =>
            {
                url.Url = "/quartz";
                url.DisplayText = "Quartz";
            })
            .PublishAsDockerFile(c => c
                .WithDockerfile(
                    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "Scheduler")),
                    "Dockerfile")
                .WithImage("localhost/froststream-scheduler", "latest"))
            .WithLocalComposeBuild("localhost/froststream-scheduler:latest", "App/Scheduler/Dockerfile");

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
        AppHostHardeningOptions hardening,
        IResourceBuilder<ProjectResource> webapi,
        AuthentikResources authentik,
        string webApiEndpointName)
    {
        // Browser-facing URLs are deployment-specific (LAN IP, reverse-proxy domain), so they are
        // parameters: the compose publisher writes them to .env instead of baking literals into
        // the yaml, letting a deployer repoint them without republishing.
        var publicOrigin = builder.AddParameter(
            "frontend-public-origin",
            FrontendPublicOrigin(),
            publishValueAsDefault: false);
        var publicAuthority = builder.AddParameter(
            "authentik-public-authority",
            FrontendPublicAuthAuthority(hardening),
            publishValueAsDefault: false);

        var frontend = builder.AddViteApp("frontend", "../Frontend")
            .WithPnpm()
            .WithExternalHttpEndpoints()
            .WithReference(webapi)
            .WaitFor(webapi)
            .WithEnvironment("VITE_API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
            .WithEnvironment("API_BASE_URL", webapi.GetEndpoint(webApiEndpointName))
            .WithEnvironment("ORIGIN", publicOrigin)
            .WithEnvironment("SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("VITE_SINGLE_USER_MODE", hardening.SingleUserMode ? "true" : "false")
            .WithEnvironment("VITE_AUTH_MODE", hardening.SingleUserMode ? "single-user" : "multi-user")
            .WithEnvironment("AUTH_CLIENT_ID", authentik.ClientId)
            .WithEnvironment("AUTH_CLIENT_SECRET", authentik.ClientSecret)
            .WithEnvironment("AUTH_SCOPES", Environment.GetEnvironmentVariable("AUTH_SCOPES") ?? "openid profile email groups offline_access")
            .WithEnvironment("AUTH_REDIRECT_URI", ReferenceExpression.Create($"{publicOrigin}/auth/callback"))
            .WithEnvironment("VITE_AUTH_PUBLIC_AUTHORITY", publicAuthority)
            .WithEnvironment("AUTH_PUBLIC_AUTHORITY", publicAuthority)
            .WithLocalComposeBuild("localhost/froststream-frontend:latest", "App/Frontend/Dockerfile");
    
        frontend = frontend.WithAuthAuthority("VITE_AUTH_AUTHORITY", hardening.SingleUserMode, authentik);
        frontend = frontend.WithAuthAuthority("AUTH_AUTHORITY", hardening.SingleUserMode, authentik);

        // Pin the host port in both modes so ORIGIN/AUTH_REDIRECT_URI stay valid whether the
        // frontend runs under Aspire, compose, or standalone `pnpm run dev` (vite.config.ts
        // defaults to the same port).
        frontend.WithEndpoint("http", endpoint => endpoint.Port = Ports.Frontend, createIfNotExists: false);
    
        if (!hardening.SingleUserMode && authentik.Server is { } authentikServer)
        {
            frontend = frontend
                .WaitFor(authentikServer)
                .WithComposeDependencyCondition("authentik", "service_healthy");
        }
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
                Context = "../..",
                Dockerfile = dockerfile
            };
        });
    }
}
