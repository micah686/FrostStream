namespace AppHost;

public sealed record OpenBaoResources(
    IResourceBuilder<ContainerResource> Server,
    IResourceBuilder<ContainerResource> DataInit,
    IResourceBuilder<ContainerResource>? DevelopmentBootstrap);

public static class OpenBaoResourceExtensions
{
    public static IResourceBuilder<T> WaitForOpenBao<T>(this IResourceBuilder<T> resource, OpenBaoResources openBao)
        where T : IResourceWithWaitSupport
        => openBao.DevelopmentBootstrap is null
            ? resource.WaitFor(openBao.Server)
            : resource.WaitForCompletion(openBao.DevelopmentBootstrap);
}

public static class StartOpenBao
{
    private const string DataVolumeName = "openbao-data";
    // The official OpenBao image runs the server as uid 100, gid 1000.
    private const string OpenBaoUserAndGroup = "100:1000";

    public static OpenBaoResources Start(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> token,
        string sharedStorageRoot)
    {
        var config = Path.Combine(builder.AppHostDirectory, "configs", "openbao", "openbao.hcl");

        var dataInit = builder
            .AddContainer("openbao-data-init", "docker.io/library/busybox", "1.37")
            .WithEntrypoint("/bin/sh")
            .WithArgs("-c", $"mkdir -p /openbao/data && chown -R {OpenBaoUserAndGroup} /openbao/data && chmod -R u+rwX,g+rwX /openbao/data")
            .WithVolume(DataVolumeName, "/openbao/data");

        var server = builder
            .AddContainer("openbao", "openbao/openbao", "2.5.5")
            .WithHttpEndpoint(port: Ports.OpenBao, targetPort: 8200, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("OPENBAO_APP_TOKEN", token)
            .WithArgs("server", "-config=/openbao/openbao.hcl")
            .WithVolume(DataVolumeName, "/openbao/data")
            .WithPortableBindMount(config, "../AppHost/configs/openbao/openbao.hcl", "/openbao/openbao.hcl", isReadOnly: true)
            .WaitForCompletion(dataInit);

        server.PublishAsDockerComposeService((_, service) =>
        {
            service.Healthcheck = new()
            {
                Test = ["CMD", "bao", "status"],
                Interval = "10s",
                Timeout = "5s",
                Retries = 30,
                StartPeriod = "10s"
            };
        });

        if (!builder.ExecutionContext.IsRunMode)
            return new OpenBaoResources(server, dataInit, null);

        var bootstrapDirectory = Path.Combine(sharedStorageRoot, "openbao-bootstrap");
        Directory.CreateDirectory(bootstrapDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                bootstrapDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var script = """
            set -eu
            until bao status >/dev/null 2>&1 || [ "$?" = "2" ]; do sleep 1; done
            if [ ! -f /bootstrap/init.env ]; then
              echo 'openbao-bootstrap: initializing development storage'
              output="$(bao operator init -key-shares=1 -key-threshold=1)"
              unseal_key="$(printf '%s\n' "$output" | sed -n 's/^Unseal Key 1: //p')"
              root_token="$(printf '%s\n' "$output" | sed -n 's/^Initial Root Token: //p')"
              umask 077
              printf 'UNSEAL_KEY=%s\nROOT_TOKEN=%s\n' "$unseal_key" "$root_token" > /bootstrap/init.env
            fi
            . /bootstrap/init.env
            if bao status >/dev/null 2>&1; then
              echo 'openbao-bootstrap: already unsealed'
            else
              bao operator unseal "$UNSEAL_KEY" >/dev/null
            fi
            if ! BAO_TOKEN="$OPENBAO_APP_TOKEN" bao token lookup >/dev/null 2>&1; then
              BAO_TOKEN="$ROOT_TOKEN" bao token create -id="$OPENBAO_APP_TOKEN" -policy=root -no-default-policy >/dev/null
            fi
            if ! BAO_TOKEN="$ROOT_TOKEN" bao secrets list -format=json | grep -q '"secret/"'; then
              BAO_TOKEN="$ROOT_TOKEN" bao secrets enable -path=secret kv-v2 >/dev/null
            fi
            echo 'openbao-bootstrap: ready'
            """.ReplaceLineEndings("\n");

        var bootstrap = builder
            .AddContainer("openbao-bootstrap", "openbao/openbao", "2.5.5")
            .WithEntrypoint("/bin/sh")
            .WithArgs("-c", script)
            .WithEnvironment("BAO_ADDR", server.GetEndpoint("http"))
            .WithEnvironment("OPENBAO_APP_TOKEN", token)
            .WithBindMount(bootstrapDirectory, "/bootstrap")
            .WaitFor(server);

        return new OpenBaoResources(server, dataInit, bootstrap);
    }
}
