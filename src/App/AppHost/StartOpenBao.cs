namespace AppHost;

// OpenBao is Vault-API-compatible. Runs in dev mode with a deterministic root token so
// services can authenticate without an unseal step. Production deployments should switch
// to AppRole + a properly-initialised cluster.
public static class StartOpenBao
{
    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> token)
    {
        return builder
            .AddContainer("openbao", "openbao/openbao", "2.5.5")
            .WithHttpEndpoint(port: Ports.OpenBao, targetPort: 8200, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", token)
            .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
            // Args accept ParameterResource (not the builder) in the compose publisher.
            .WithArgs("server", "-dev", "-dev-root-token-id", token.Resource);
    }
}
