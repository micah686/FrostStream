namespace AppHost;

public sealed record OpenBaoResources(
    IResourceBuilder<ContainerResource> Server,
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
    // /openbao/file is a path the official image owns and fixes up before it drops privileges.
    // Mounting the Raft volume there lets the image handle first-use volume ownership itself.
    private const string DataDirectory = "/openbao/file";
    private const string BootstrapDirectory = "/bootstrap";

    public static OpenBaoResources Start(
        IDistributedApplicationBuilder builder,
        string sharedStorageRoot,
        IResourceBuilder<ParameterResource> token)
    {
        var config = Path.Combine(builder.AppHostDirectory, "configs", "openbao", "openbao.hcl");
        var bootstrapRoot = OpenBaoBootstrapPaths.HostRoot(sharedStorageRoot);

        var server = builder
            .AddContainer("openbao", "openbao/openbao", "2.5.5")
            .WithHttpEndpoint(port: Ports.OpenBao, targetPort: 8200, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("OPENBAO_APP_TOKEN", token)
            .WithArgs("server", "-config=/openbao/openbao.hcl")
            .WithVolume(DataVolumeName, DataDirectory)
            .WithPortableBindMount(config, "../AppHost/configs/openbao/openbao.hcl", "/openbao/openbao.hcl", isReadOnly: true);

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

        var script = """
            set -eu

            bootstrap_file="/bootstrap/init.env"
            legacy_bootstrap_file="/openbao/file/.bootstrap/init.env"

            write_bootstrap_file() {
              temp_file="${bootstrap_file}.tmp"
              umask 077
              printf 'UNSEAL_KEY=%s\nROOT_TOKEN=%s\n' "$1" "$2" > "$temp_file"
              mv "$temp_file" "$bootstrap_file"
            }

            until bao operator init -status >/dev/null 2>&1 || [ "$?" -eq 2 ]; do sleep 1; done
            if bao operator init -status >/dev/null 2>&1; then
              if [ ! -f "$bootstrap_file" ]; then
                if [ -f "$legacy_bootstrap_file" ]; then
                  echo 'openbao-bootstrap: migrating recovery material from the data volume'
                  temp_file="${bootstrap_file}.tmp"
                  umask 077
                  cat "$legacy_bootstrap_file" > "$temp_file"
                  mv "$temp_file" "$bootstrap_file"
                  rm -f "$legacy_bootstrap_file"
                  rmdir /openbao/file/.bootstrap 2>/dev/null || true
                else
                  echo 'openbao-bootstrap: vault is initialized but /bootstrap/init.env is missing; restore it from backup before starting dependent services' >&2
                  exit 1
                fi
              elif [ -f "$legacy_bootstrap_file" ]; then
                echo 'openbao-bootstrap: removing legacy recovery material from the data volume'
                rm -f "$legacy_bootstrap_file"
                rmdir /openbao/file/.bootstrap 2>/dev/null || true
              fi
              echo 'openbao-bootstrap: using existing initialization'
            else
              echo 'openbao-bootstrap: initializing development storage'
              output="$(bao operator init -key-shares=1 -key-threshold=1)"
              unseal_key="$(printf '%s\n' "$output" | sed -n 's/^Unseal Key 1: //p')"
              root_token="$(printf '%s\n' "$output" | sed -n 's/^Initial Root Token: //p')"
              write_bootstrap_file "$unseal_key" "$root_token"
            fi
            . "$bootstrap_file"
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
            // The recovery material belongs on a host bind mount so it can be backed up separately
            // from the Raft volume. During the migration release this container also sees the data
            // volume only to move a pre-existing .bootstrap/init.env out of it and delete the copy.
            .WithPortableBindMount(
                bootstrapRoot,
                "${FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT:-./openbao-bootstrap}",
                BootstrapDirectory)
            .WithVolume(DataVolumeName, DataDirectory)
            .WaitFor(server);

        return new OpenBaoResources(server, bootstrap);
    }
}
