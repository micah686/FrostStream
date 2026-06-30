namespace AppHost;

// OpenBao is Vault-API-compatible. Runs in dev mode with a deterministic root token so
// services can authenticate without an unseal step. Production deployments should switch
// to AppRole + a properly-initialised cluster.
public static class StartOpenBao
{
    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening)
    {
        return builder
            .AddContainer("openbao", "openbao/openbao", hardening.OpenBaoImageTag)
            .WithHttpEndpoint(port: 8200, targetPort: 8200, name: "http")
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", hardening.OpenBaoToken)
            .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
            .WithArgs("server", "-dev", "-dev-root-token-id", hardening.OpenBaoToken);
    }
}
