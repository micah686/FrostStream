namespace AppHost;

public sealed record OpenBaoResources(
    IResourceBuilder<ContainerResource> Server,
    IResourceBuilder<ContainerResource>? Initialization);

public static class OpenBaoResourceExtensions
{
    public static IResourceBuilder<T> WaitForOpenBao<T>(this IResourceBuilder<T> resource, OpenBaoResources openBao)
        where T : IResourceWithWaitSupport, IComputeResource
    {
        if (openBao.Initialization is not null)
            return resource.WaitForCompletion(openBao.Initialization);

        return resource
            .WaitFor(openBao.Server)
            .WithComposeDependencyCondition(DeploymentRuntime.Current.Names.OpenBao, "service_healthy");
    }
}

public static class StartOpenBao
{
    // /openbao/file is a path the official image owns and fixes up before it drops privileges.
    // Mounting the Raft volume there lets the image handle first-use volume ownership itself.
    private const string DataDirectory = "/openbao/file";
    private const string BootstrapDirectory = "/bootstrap";

    public static OpenBaoResources Start(
        IDistributedApplicationBuilder builder,
        string sharedStorageRoot,
        IResourceBuilder<ParameterResource> token)
    {
        var deployment = DeploymentRuntime.Current;
        var config = deployment.Paths.AppHostConfig("openbao", "openbao.hcl");
        var bootstrapRoot = OpenBaoBootstrapPaths.HostRoot(sharedStorageRoot);
        var dataVolumeName = deployment.Names.Volume("openbao-data");
        var includeInitialization = deployment.Selection.Profile.IncludeInitialization;
        if (builder.ExecutionContext.IsRunMode)
            Directory.CreateDirectory(bootstrapRoot);

        var serverScript = """
            set -eu

            bootstrap_file="/bootstrap/init.env"
            init_allowed="${OPENBAO_INIT_ALLOWED:-false}"

            bao server -config=/openbao/openbao.hcl &
            server_pid=$!

            shutdown() {
              trap - TERM INT
              kill -TERM "$server_pid" 2>/dev/null || true
              wait "$server_pid" 2>/dev/null || true
            }
            fail() {
              echo "openbao: $1" >&2
              kill -TERM "$server_pid" 2>/dev/null || true
              wait "$server_pid" 2>/dev/null || true
              exit 1
            }
            trap shutdown TERM INT

            state=""
            attempt=1
            while [ "$attempt" -le 120 ]; do
              if ! kill -0 "$server_pid" 2>/dev/null; then
                wait "$server_pid"
                exit $?
              fi
              set +e
              bao operator init -status >/dev/null 2>&1
              status=$?
              set -e
              if [ "$status" -eq 0 ]; then
                state="initialized"
                break
              fi
              if [ "$status" -eq 2 ]; then
                # Raft can briefly report "not initialized" while it restores its persisted
                # barrier state. Recovery material means this installation is expected to be
                # initialized, so keep polling instead of misclassifying a normal restart.
                if [ ! -f "$bootstrap_file" ]; then
                  state="uninitialized"
                  break
                fi
              fi
              sleep 1
              attempt=$((attempt + 1))
            done
            [ -n "$state" ] || fail "API did not become ready within 120 seconds"

            if [ "$state" = "uninitialized" ]; then
              [ "$init_allowed" = "true" ] || fail "storage is uninitialized; run profile 'frostream-full-init'"
              echo 'openbao: awaiting explicit first-time initialization'
            else
              [ -f "$bootstrap_file" ] || fail "recovery material is missing; restore /bootstrap/init.env from backup"
              . "$bootstrap_file"
              if bao status >/dev/null 2>&1; then
                echo 'openbao: already unsealed'
              else
                bao operator unseal "$UNSEAL_KEY" >/dev/null || fail "unseal failed"
                echo 'openbao: unsealed from persisted recovery material'
              fi
            fi

            wait "$server_pid"
            """.ReplaceLineEndings("\n");

        var server = builder
            .AddContainer(deployment.Names.OpenBao, deployment.Images.OpenBao.Repository, deployment.Images.OpenBao.Tag)
            .WithHttpEndpoint(port: Ports.OpenBao, targetPort: 8200, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("OPENBAO_APP_TOKEN", token)
            .WithEnvironment("OPENBAO_INIT_ALLOWED", includeInitialization ? "true" : "false")
            .WithEnvironment("BAO_ADDR", "http://127.0.0.1:8200")
            .WithEntrypoint("/bin/sh")
            .WithArgs("-c", serverScript)
            .WithVolume(dataVolumeName, DataDirectory)
            .WithPortableBindMount(
                bootstrapRoot,
                "${FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT:-./openbao-bootstrap}",
                BootstrapDirectory,
                isReadOnly: true)
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
            service.Restart = "unless-stopped";
        });

        if (!includeInitialization)
            return new OpenBaoResources(server, Initialization: null);

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

            ready=false
            attempt=1
            while [ "$attempt" -le 120 ]; do
              set +e
              bao operator init -status >/dev/null 2>&1
              status=$?
              set -e
              if [ "$status" -eq 0 ]; then
                ready=true
                break
              fi
              if [ "$status" -eq 2 ]; then
                ready=true
                break
              fi
              sleep 1
              attempt=$((attempt + 1))
            done
            [ "$ready" = true ] || { echo 'openbao-bootstrap: API did not become ready within 120 seconds' >&2; exit 1; }
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
            .AddContainer(deployment.Names.OpenBaoBootstrap, deployment.Images.OpenBao.Repository, deployment.Images.OpenBao.Tag)
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
            .WithVolume(dataVolumeName, DataDirectory)
            .WaitFor(server)
            .PublishAsDockerComposeService((_, service) => service.Restart = "no");

        return new OpenBaoResources(server, bootstrap);
    }
}
