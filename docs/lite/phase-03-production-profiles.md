# Phase 3: production profiles and installation configuration

Date verified: 2026-09-11

Status: Verified, awaiting acceptance.

Phase 3 connects the proven profile and Compose APIs to the production AppHost. It centralizes deployment inputs and proves sanitized Full publication. It does not implement the Lite graph or split Full initialization from recurring runtime.

## Production selection

The AppHost creates its builder to identify run versus publish mode, then resolves the selected environment file, profile, and deployment configuration before adding any resources.

- `aspire run` defaults to `frostream-full-init` and implicitly loads `AppHost/aspire-development.env`.
- Publish mode requires `--deployment-profile` or `FROSTSTREAM_DEPLOYMENT_PROFILE`.
- `--deployment-env <path>` or `FROSTSTREAM_ENV_FILE` explicitly selects an env file in either mode.
- `--developer-tools true` or `FROSTSTREAM_DEV_TOOLS=true` remains separate from the immutable profile.
- `FROSTSTREAM_INSTALLATION_NAME` overrides the installation name; the default is `froststream-full` or `froststream-lite` based on edition.
- Production Lite profiles fail before deployment configuration is persisted or resources are registered. Their production composition begins in Phase 8.

The existing public `generateCompose.sh` and `generateCompose.ps1` workflows remain unchanged for now. `eng/publish-production-profile.sh` is a Phase 3 verification entry point and accepts only the two Full profile names.

## Input precedence and persistence

The resolver applies values in this order:

1. Current process environment and explicit AppHost arguments.
2. An explicitly selected env file, or the development file during local run mode.
3. The installation's preserved resolved env file.
4. Edition defaults and newly generated missing secrets.

Blank values in templates do not replace preserved values. On first publish, missing deployment secrets are generated. Later init/runtime publications for the same installation reuse them.

Resolved values are written atomically with mode `0600` to:

```text
src/App/AppHost/deployment/resolved/<installation-name>/resolved.env
```

That directory is ignored by Git. The persisted file excludes the selected profile, developer-tools option, and publish output directory because those belong to an invocation rather than a live installation. Publishing to another directory therefore does not change the installation state.

Committed templates contain no populated credentials:

- `src/App/AppHost/deployment/templates/full.env.example`
- `src/App/AppHost/deployment/templates/lite.env.example`

## Central deployment model

`DeploymentConfiguration` owns:

- Compose project, resource, database, network, and volume names. The default Full installation retains its established volume IDs; custom and Lite installations use installation-prefixed IDs.
- Container repositories, tags, and local application-image naming.
- External and internal ports.
- Source/build context and Dockerfile paths.
- Storage, backup, OpenBao recovery, resolved-state, and publish paths.

The production startup factories now consume this model. For default Full publication, representative native output is:

```yaml
name: "froststream-full"
services:
  postgres:
    image: "localhost/froststream-postgres:latest"
    build:
      context: "../../src"
      dockerfile: "App/PostgresServer/Dockerfile"
    volumes:
      - source: "froststream-postgres-data"
        target: "/var/lib/postgresql"
    networks:
      - "froststream-full-network"
networks:
  froststream-full-network:
volumes:
  froststream-postgres-data:
```

Config bind paths are calculated relative to the selected output directory. Persistent backup and OpenBao recovery mounts resolve to absolute paths beneath the installation storage root unless explicitly configured. This prevents changing the bundle directory from silently selecting different persistent data.

The default Full installation keeps the existing repository `data` root and established volume names (`froststream-postgres-data`, `openbao-data`, `typesense-data`, and related shared volumes). This avoids silently switching an existing Full installation to empty storage. A custom installation name uses its own `data/<installation>` path and prefixed volumes.

Aspire owns output changes through `AddDockerComposeEnvironment`, `WithDashboard(false)`, `ConfigureComposeFile`, `ConfigureEnvFile`, and `PublishAsDockerComposeService`. No generated YAML or env file is modified.

## Reproduction

From the repository root:

```bash
dotnet tool restore

./eng/publish-production-profile.sh \
  frostream-full-init \
  artifacts/phase3-production-full-init

./eng/publish-production-profile.sh \
  froststream-full \
  artifacts/phase3-production-full

dotnet run \
  --project src/App/ComposePublishingFixture.Tests/ComposePublishingFixture.Tests.csproj \
  -- --production-pair \
  artifacts/phase3-production-full-init \
  artifacts/phase3-production-full \
  froststream-full \
  src \
  src/App/AppHost/deployment/resolved/froststream-full/resolved.env
```

The publishing script restores `src/App/aspire.config.json` after the Aspire CLI updates its selected-AppHost setting.

To use a private input file:

```bash
./eng/publish-production-profile.sh \
  frostream-full-init \
  /absolute/output/full-init \
  /absolute/private/full.env
```

## Verification evidence

The following passed across 2026-09-10 and 2026-09-11:

```text
dotnet build src/App/FrostStream.slnx --no-restore --verbosity minimal --disable-build-servers
Build succeeded. 0 Warning(s), 0 Error(s).

Deployment configuration verifier
- process/file/preserved/default precedence: passed
- selected-file profile and developer-tools selection: passed
- Full pair project/network/volume/path/credential identity: passed
- independent installation isolation: passed
- default and alternate output/build-context resolution: passed
- invalid Full/Lite configuration errors with secret redaction: passed
- atomic resolved-file round trip and 0600 permissions: passed

Production publication
- frostream-full-init native publish: passed
- froststream-full native publish: passed
- both docker compose config validations: passed
- Full pair graph/secret-leak verifier: passed
- developer tools and dashboard absent: passed
```

An additional `froststream-full` publication to `/tmp/froststream-phase3/compatibility-final/runtime` passed Compose and path validation. Before and after that publication, the resolved installation file SHA-256 was:

```text
575afd5e89aedea7f283849ce43d4434a3646f65beea494e3c7dadab494db37b
```

This proves that changing disposable output and switching between the Full pair does not regenerate or rewrite live installation inputs.

A `frostream-lite` production publish failed before any resource creation with the explicit Phase 8 instruction, and no Lite resolved file was created.

## Current limits

The two Full profiles currently select the same legacy Full graph, including its existing one-shot resources. Phase 4 extracts explicit initialization and Phase 5 makes `froststream-full` a true runtime-only graph. Until then, use this phase's output for inspection and publisher verification rather than init/runtime handoff.

The production Lite graph remains unavailable. The sanitized Lite template establishes its separate installation namespace and future inputs without claiming deployability.

The public Compose generation scripts still perform their existing transformation workflow. Replacing them is intentionally deferred until the later generation-workflow phase.

## Rollback

Remove the deployment bootstrap/runtime integration and templates, restore the AppHost's prior environment loading and startup constants, remove the Deployment project reference, and delete the Phase 3 verification script and tests. Ignored resolved and publication files can be discarded without touching existing container volumes. Restoring source does not require exposing or committing the resolved credential file.
