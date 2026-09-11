# FrostStream Lite implementation plan

Status: proposed implementation plan; no runtime changes are made by this document.

## 1. Scope and decisions

Create a Lite edition alongside the existing Full edition. Lite runs all FrostStream .NET application responsibilities in one process and one container, supports exactly one local owner, and has no application authentication or remote workers.

Required Lite infrastructure: PostgreSQL, ClickHouse, and Typesense. Remove Authentik and OpenBao. This plan also removes OpenFGA and NATS from Lite: authorization infrastructure and an inter-service message broker are unnecessary for the intended single-process deployment. Full retains its distributed architecture and existing identity/secrets capabilities.

Keep downloads, creator subscriptions, imports, library management, search, playback, casting, live chat, media processing, scheduling, and backups. Single-user does not mean a single storage target or removal of existing media editions. Preserve those data models and capabilities.

Generate four independent Compose + environment-file bundles through Aspire, without shell or PowerShell modification of generated YAML or env files:

| Requested environment name | Edition | Contents |
| --- | --- | --- |
| `frostream-full-init` | Full | Full runtime resources plus applicable initialization jobs |
| `froststream-full` | Full | Full runtime resources only |
| `frostream-lite-init` | Lite | Lite runtime resources plus applicable initialization jobs |
| `frostream-lite` | Lite | Lite runtime resources only |

The names above intentionally preserve the spelling supplied in the request, including `froststream-full`. Keep internal product namespaces spelled `FrostStream`; map external environment names explicitly rather than deriving them through string replacement.

An init environment is a complete first-start stack, not an override that users must layer onto another file. Its successful jobs exit and are absent from the corresponding normal-start bundle. Versioned initialization may be invoked again for an upgrade or recovery; it is never an automatic prerequisite for every ordinary restart.

## 2. Repository findings that shape the work

| Existing code | Relevant behavior and planned change |
| --- | --- |
| `src/App/AppHost/AppHost.cs` | Registers one `aspire-docker-demo` Compose environment. Always provisions NATS and OpenBao. Replace with explicit edition/lifecycle composition. |
| `src/App/AppHost/StartServices.cs` | Wires WebAPI, DataBridge, Worker, MediaProcessor, Scheduler, and Frontend. Add a Lite composition path with one backend resource. |
| `src/App/AppHost/StartBackupService.cs` | Adds a separate .NET backup container with PostgreSQL data/socket mounts and OpenBao integration. Merge its Lite responsibilities too. |
| `src/App/generateCompose.sh` and `generateCompose.ps1` | Publish development/normal variants and rewrite artifacts afterward. Replace artifact transformation with Aspire model configuration and publishing. |
| `src/App/AppHost/StartPostgres.cs` | Creates `backup-init` and `postgres-init` during publishing; declares FrostStream, Authentik, and OpenFGA databases. Make database lists and init resources profile-specific. |
| `src/App/AppHost/StartOpenBao.cs` | `openbao-bootstrap` combines initialization, recovery-material handling, unsealing, and configuration. Split first-time work from restart requirements. |
| `src/App/AppHost/StartOpenFga.cs` | Runs `openfga-migrate` and waits for completion. Include that job only in Full init; validate compatible schema during normal startup. |
| `src/App/DataBridge/Program.cs` | Owns FluentMigrator, PostgreSQL-backed Cleipnir flows, numerous NATS consumers, and search/chat startup services. Extract application operations and explicit initialization. |
| `src/App/Shared/Secrets/ISecretStore.cs` | Already abstracts reads, writes, and deletes. Add a local implementation for Lite while retaining OpenBao for Full. |
| `src/App/WebAPI/Auth/SingleUserAuthenticationHandler.cs` | Current single-user mode synthesizes an authenticated principal. Lite should instead resolve a fixed owner without requiring authentication middleware. |
| `src/App/DataBridge/Messaging/SingleUserOwnerSeederService.cs` | Existing fixed owner identity provides a compatibility anchor for persisted records. Move initial seeding into initialization. |
| `src/App/Frontend/svelte.config.js` and `Dockerfile` | Frontend already builds a static SPA served by Caddy. Lite can serve that build from ASP.NET Core, eliminating the separate frontend runtime container. |
| `src/App/BackupService/Program.cs` | Restore is intentionally able to run while the database is offline. Preserve that property in a dedicated command mode of the merged image. |

Use implementation code as the inventory source: the README currently describes MediaProcessor as unfinished, but its program registers rendition and thumbnail processing services.

## 3. Lite runtime architecture

```text
Browser / REST clients / casting devices
                  |
          FrostStream.Lite
          one ASP.NET Core process
          - static frontend + HTTP API + streaming + SSE
          - catalog, imports, download orchestration
          - local download and media-processing executors
          - schedules, search indexing, live-chat ingestion
          - secret storage and backup coordination
                  |
       PostgreSQL / ClickHouse / Typesense
                  |
       persistent media, secrets, keys, backups
```

The baseline is four long-running containers: the merged application and the three databases/search services. Preserve the existing bgutil POT provider as an optional fifth container for YouTube functionality, enabled when that capability is configured. Replace the current NATS POT broker/shim transport with direct local application/provider calls. Do not hide loss of POT support behind a reduced container count.

ClickHouse is included in both Lite bundles regardless of the current optional `LIVE_CHAT_ENABLED` provisioning behavior. Chat ingestion can still be toggled independently. Developer dashboards, database browsers, and NATS tooling are excluded from the four deployment bundles by default; developer tooling is a separate run-mode option.

### Merge services through reusable modules

Add `src/App/FrostStream.Lite/` with one entry point and one Dockerfile. Extract reusable registration and application services from the existing executables into libraries/modules. Keep the Full executables as composition roots that register their existing transport adapters.

| Existing executable | Responsibility inside Lite |
| --- | --- |
| WebAPI | HTTP routes, streaming, casting, SSE, and frontend hosting |
| DataBridge | Persistence, workflows, catalog operations, imports, search, and chat |
| Worker | Local yt-dlp execution, artifact storage, cookie materialization, and discovery |
| MediaProcessor | In-process orchestration of ffmpeg/ffprobe and derived artifacts |
| Scheduler | Embedded Quartz scheduler and local job dispatch |
| BackupService | Embedded backup coordination plus offline restore command mode |

Do not invoke existing `Program.Main` methods or host multiple service processes inside one container. Consolidate dependency injection, logging, HTTP clients, options, readiness, and graceful shutdown. Resolve controller discovery, route collisions, scoped DbContext lifetimes, and duplicate hosted-service registration explicitly.

Build the static frontend during the image build and copy it into the merged application's static assets. Serve SPA fallback only for frontend navigation; API, SSE, media, and health routes retain their normal status codes and streaming behavior. Preserve the public frontend port and casting URL configuration. Keep Node/pnpm in build stages; include yt-dlp dependencies, ffmpeg/ffprobe, and required backup tools in the runtime image.

### Replace transport without losing durable work

Extract operations behind typed interfaces so Full NATS handlers and Lite direct calls invoke the same business logic. Inventory request/reply, JetStream commands, event consumers, progress hubs, workflow ingress, schedule-change notifications, and backup reporting before replacing each path.

- Use direct asynchronous service calls for synchronous queries and commands.
- Preserve PostgreSQL-backed Cleipnir state and persisted job records for long-running work. Adapt the existing workflows to local executors instead of replacing them with an unpersisted queue.
- Use bounded in-process channels for wake-ups and progress delivery only. Commit durable intent before acknowledging a job submission; recover pending work from PostgreSQL after a crash.
- Define retries, cancellation, deduplication, durable checkpoints, and restart recovery for each job category. Persist enough state to reconcile files created before a process failure and to retry indexing/chat ingestion safely.
- Publish SSE updates through a local event hub with bounded subscribers. Reconnected clients obtain an authoritative persisted snapshot; do not promise lossless delivery from an in-memory channel.
- Use one execution owner and bounded local concurrency, initially one download at a time. Remove remote worker registration, heartbeat, queue groups, tag routing, and distributed lease monitoring from Lite.
- Reject a second Lite execution instance for the same database with an exclusive PostgreSQL advisory lock held on a dedicated connection. If that connection/lock is lost, stop dispatch and cancel execution before attempting recovery.

Retain legacy worker-related columns where necessary for migration compatibility, but do not advertise remote execution. Normalize existing storage-affinity settings to the local executor during an explicit import. Keep Quartz scheduling in-process and recover persisted schedules before enabling dispatch.

### Single user with no authentication

Reuse the existing fixed owner ID for foreign keys, preferences, watch state, cookies, playlists, and notifications. Introduce a current-owner abstraction that returns that ID in Lite and uses the existing authenticated identity in Full.

Lite registers no OIDC, JWT, cookie authentication, OpenFGA client, session ticket store, user provisioning, or authentication challenge handler. Audit authorization filters, controller attributes, internal media-processing credentials, and endpoints that currently assume claims exist. Route Lite requests directly to the fixed owner rather than relying on a fabricated login.

Provide an explicit edition/capabilities response for the frontend. Update `Frontend/src/routes/+layout.ts` and related UI so Lite does not call login/session-refresh flows or display logout, users, roles, access policies, or worker management. Preferences remain available. During migration, `/api/auth/me` may return a fixed-owner compatibility response without creating a session.

Lite deployment validation must accept unauthenticated operation in the Production runtime environment without requiring fake Authentik/OpenBao values or weakening Full's validation. Service credentials for PostgreSQL, ClickHouse, Typesense, and configured remote storage remain necessary; “no authentication” describes the application user experience.

### Local secrets without OpenBao

Implement `ISecretStore` with an encrypted local file store under a persistent secrets directory. Preserve `SecretPaths` and `StorageSecretSplitter` semantics so storage credentials and cookie profiles remain usable through the same application services.

Use ASP.NET Core Data Protection with a persisted key ring and a stable application name/purpose; this is for encryption, not login. Use atomic file replacement, bounded/path-safe key mapping, serialized writes, restrictive permissions, and redacted diagnostics. Document that a key ring stored beside ciphertext protects against accidental plaintext exposure, not an attacker who can read both.

Back up and restore ciphertext and the key ring together. A missing key ring must produce a clear recovery error instead of silently creating replacement credentials. Implement an explicit Full-to-Lite export/import of only the selected owner's secrets; never export OpenBao root tokens or unseal material into Lite config.

### Backups and offline restore

Move backup scheduling and coordination into the merged process and replace internal backup HTTP/NATS calls with local services. Retain pgBackRest-compatible tools, configuration, PostgreSQL version compatibility, data/socket mounts, and required filesystem ownership. Remove OpenBao pairing from the Lite backup manifest and include local secrets/key material instead.

Provide `backup` and `restore` command modes in the same application image. Restore mode must start without database readiness, workflow services, or web authentication and must run while the normal application and PostgreSQL are stopped. Use a local command workflow for Lite restore rather than carrying over the token-gated restore server. No separate long-running .NET backup container remains.

Describe backup coverage precisely: PostgreSQL, media, local secrets/key ring, ClickHouse data, and Typesense reconstruction. Retain archived chat source artifacts and document rebuilding ClickHouse where supported; otherwise back up irreplaceable chat data explicitly. Verify an actual restore, not just backup command success.

## 4. Aspire-owned Compose and env generation

### Model four explicit profiles

Introduce an immutable `DeploymentProfile` containing external name, edition, and `IncludeInitialization`, plus a separate developer-tools option. Resolve the profile before loading profile-dependent configuration or adding resources. Add proposed `DeploymentProfiles.cs`, `ComposePublishing.cs`, and `StartLiteServices.cs` under AppHost.

Use a fresh AppHost resource graph for each selected profile. Share infrastructure factory code, but create only resources belonging to that profile. Register its native `AddDockerComposeEnvironment(profile.Name)` and let Aspire publish it. This avoids a global resource graph accidentally placing Full services or secrets in Lite output.

Add a cross-platform .NET/MSBuild publish-all entry point that invokes Aspire for each of the four profiles with an explicit output directory. It orchestrates native Aspire publishing only; it must not parse, patch, rename, or merge YAML/env output. All differences originate in the AppHost graph or Aspire's typed callbacks. A single invocation of that entry point produces all four bundles.

Do not assume that registering four Compose environments automatically filters resources or that the CLI `--environment` flag alone produces four different topologies. First prove profile selection, native output placement, and parameter resolution with the repository's installed Aspire version. Pin the SDK/package/CLI versions used by the generator; AppHost currently mixes SDK `13.2.2` with hosting packages `13.4.6`.

Proposed artifact layout, keeping native filenames:

```text
src/App/docker-compose-artifacts/
  frostream-full-init/docker-compose.yaml + .env
  froststream-full/docker-compose.yaml    + .env
  frostream-lite-init/docker-compose.yaml + .env
  frostream-lite/docker-compose.yaml      + .env
```

### Configure output before serialization

- Use `WithDashboard(false)` on deployment environments instead of removing dashboard YAML afterward.
- Use `PublishAsDockerComposeService` for build contexts, image names, restart policies, health checks, ports, and dependency conditions.
- Use `ConfigureComposeFile` for the Compose project name and shared volume/network naming rules.
- Use Aspire parameters and `ConfigureEnvFile` for configuration placeholders, descriptions, and safe defaults. Declare parameters only for resources in the selected profile.
- Resolve deployment inputs through one edition-level source shared by its init/runtime profiles. Init must not mint a database password or Typesense key that differs from runtime. Keep profile selection separate from `DOTNET_ENVIRONMENT`.
- Stop the current unconditional development env load from overriding explicit deployment settings. Define precedence as explicit publish inputs/environment, then profile defaults; load development values only for local run mode or an explicitly selected file.
- Distinguish native publication of an unfilled `.env` contract from preparation of resolved values. The implementation spike must prove how the pinned Aspire version emits the requested final bundle through native publish/prepare hooks. Keep populated secrets in ignored deployment output and commit only sanitized templates.
- Preserve user-managed deployment values when regenerating templates. No overwrite of a live installation's credentials and no copying development secrets into examples.
- Audit relative paths when adding subdirectories: current build contexts use `../..`, and config mounts use `../AppHost/...`. Compute their new locations centrally and test publishing into a different destination too.

The installed Docker hosting package exposes the callbacks above and `WithDashboard`. Aspire's documented publish/prepare distinction and environment behavior are relevant to the implementation spike; environment selection commonly shares a Compose filename, so separate output directories are essential here. See [Aspire Docker integration](https://aspire.dev/integrations/compute/docker/) and [Compose publishing and deployment](https://aspire.dev/deployment/docker-compose/).

### Preserve installation identity across init and runtime

Use the same Compose project name for each pair: proposed defaults `froststream-full` and `froststream-lite`. The `-init` suffix identifies an artifact/profile, never a separate installation. Support an installation-name override to avoid collisions between independent installations.

Pair members must have identical runtime service names, image references, port bindings, named-volume identities, network identities, and resolved persistent host paths. Use absolute configured backup/media paths or a shared installation directory so changing bundle directories does not silently select a new `./backups` directory. Full and Lite defaults use separate data volumes; switching editions is a migration, not simultaneous use of the same database.

After initialization succeeds, start the normal bundle under the same project name. Remove only the completed init containers or use scoped orphan cleanup once identity checks pass. Never use `down -v` or volume-destroying Aspire cleanup for this handoff.

## 5. Initialization versus recurring startup

| Work | Init bundle | Normal runtime |
| --- | --- | --- |
| Backup/media/secrets directory ownership | Explicit idempotent preparation where required | Validate writable mounts; do not require `backup-init` |
| PostgreSQL database creation | Only databases required by that edition | Wait for healthy PostgreSQL; validate required database exists |
| Application schema and fixed-owner seed | Versioned initializer, Lite using the same merged image with `initialize` command | Validate compatible schema and initialization version before becoming ready |
| ClickHouse schema and Typesense collection setup | Explicit versioned setup | Connect, query, ingest/index; validate compatibility |
| Cleipnir schema/runtime tables | Provision required persistent state | Resume workflows; distinguish harmless library checks from first-time setup |
| Full OpenFGA migrations | `openfga-migrate` | No dependency on the omitted migration container |
| Full OpenBao initialization/policy setup | First-time bootstrap and recovery-material creation | Separate recurring unseal path; no first-time bootstrap container |
| Full Authentik migrations | Retain supported vendor lifecycle unless a separate supported job is verified | Do not invent an init-only migration mechanism for its normal entrypoint |
| Backup stanza creation | Explicit idempotent initialization | Validate stanza, execute scheduled backups |

Refactor `WaitForDatabases`, `WaitForOpenBao`, and related resource records to make init dependencies optional. Normal bundles must not contain a `depends_on` edge, endpoint expression, env reference, or mount referring to an omitted resource.

Full OpenBao is the critical restart case: its existing bootstrap script unseals an already initialized vault. Move unsealing into a supported server startup wrapper/lifecycle that runs on each restart, or a configured auto-unseal mechanism. For the existing development recovery-file model, a supervised startup wrapper can unseal from persisted recovery material without initializing a new vault. Handle process signals and readiness correctly. Confirm normal Full starts after container recreation with all init containers removed.

Initialization jobs use `restart: "no"`, explicit dependencies, finite retry deadlines, nonzero failure exits, and idempotent operations. Dependent applications use successful-completion conditions in init bundles and actual infrastructure health conditions in normal bundles. Initialization success is a versioned durable record written only after all required steps complete.

Normal startup on empty/incompatible storage should fail with the exact matching init-profile instruction. Normal startup still performs health checks, reconnects, schedule hydration, workflow recovery, and incremental index synchronization. Those are recurring runtime duties, not initialization jobs.

For upgrades requiring schema changes, stop affected application execution, back up data, and explicitly rerun the matching version's init bundle. No silently skipped migrations and no repeated destructive seeding. Document recovery from partial initialization and permit safe retries.

## 6. Implementation sequence

1. **Prove Compose publishing first.** Add the profile model and a small publish fixture proving four native Compose/env bundles, dashboard exclusion, parameter resolution, init/runtime identity, and conditional dependencies. Confirm pinned Aspire APIs before committing to publish-all command syntax. Do not modify generated artifacts to make the proof pass.
2. **Split lifecycle in Full.** Refactor existing infrastructure factories, database selection, one-shot jobs, and OpenBao restart unsealing. Publish and validate both Full bundles while preserving existing application behavior.
3. **Extract reusable backend operations.** Separate business logic from NATS consumers and executable startup. Inventory every message path and associate it with a direct call, persisted workflow, or local event. Keep Full adapters and contract tests passing.
4. **Build the merged Lite host.** Compose catalog/API, local execution, processing, scheduler, and backups; embed the static frontend. Add durable dispatch recovery, bounded concurrency, and exclusive execution ownership. Remove Lite dependencies on NATS and service-to-service HTTP.
5. **Complete fixed-owner and local-secret behavior.** Add owner resolution, remove authentication and worker-management paths, update frontend capabilities, and implement secret persistence/backup. Exercise cookies and credentialed storage end to end.
6. **Add explicit initialization and restore modes.** Move schema/setup responsibilities out of ordinary startup, integrate version checks, and ensure offline restore does not start regular hosted services. Publish both Lite bundles.
7. **Validate edition migration.** Provide an offline Full-to-Lite import selecting one owner; map owner references and secret paths, normalize worker affinity, preserve media/storage references, and rebuild derived indexes. Reject ambiguous multi-user merges. Drain/cancel or explicitly reconcile pending distributed work before migration; do not reinterpret in-flight NATS messages as completed jobs. Preserve original Full data for rollback.
8. **Replace generation workflow and document operations.** Remove transformations from `generateCompose.sh`/`.ps1`, update startup helpers and README, provide sanitized env templates, and document first start, normal start, upgrades, backup, restore, and migration. Any retained launcher may invoke the new publisher but contains no artifact-editing logic.

Each step should land as a reviewable change with its relevant validation. The plan does not require rewriting shared contracts or removing the existing Full executables.

## 7. Acceptance criteria

### Artifact and lifecycle checks

- One cross-platform generation command emits all four named directories, each containing Aspire-generated Compose and env output. Repeated generation with identical inputs is stable apart from documented publisher metadata.
- Every bundle passes `docker compose --env-file <file> -f <compose> config --quiet`. Parsed graph tests confirm service membership, defined dependencies, matching runtime definitions within a pair, and absence of Full-only parameters in Lite.
- Lite contains exactly one persistent .NET application service, PostgreSQL, ClickHouse, Typesense, and only the explicitly configured optional POT provider. Its normal bundle contains no init services, NATS, OpenBao, Authentik, OpenFGA, or separate Worker/Scheduler/MediaProcessor/BackupService/Frontend containers.
- Empty-volume init succeeds; deliberate init failure prevents application readiness; retry succeeds without duplicate seeds or credential changes.
- Switching to normal artifacts preserves database records, media, secrets, keys, backups, and search/chat state. Recreating all runtime containers succeeds with init containers absent. Full OpenBao unseals through its runtime path.
- Normal startup on empty storage fails clearly. An upgrade requiring migration fails clearly until the explicit init/upgrade procedure runs.
- Build contexts and bind mounts resolve from the generated bundle directories and an alternate publish output directory. Live env values are not overwritten during regeneration.

### Application and recovery checks

- Lite browser/API access requires no login, token, session cookie, or identity provider; all persisted user-owned features resolve to the fixed owner. Removed authentication/worker controls do not appear in the UI.
- Download → ingest → search → playback works, including progress SSE, cancellation, retry, creator scans, playlist expansion, import, rendition processing, casting, cookies, credentialed storage, and live-chat replay.
- Kill the merged process during download, artifact commit, indexing, and schedule execution. Restart recovers durable intent without duplicate catalog commits or permanently stuck jobs. A second execution instance is rejected, including lock-loss handling.
- Local secrets survive restart and backup/restore; missing keys and malformed paths fail predictably. Logs and generated examples contain no real credentials.
- Restore using the merged image works with regular services stopped. Validate restored media/catalog links and the documented ClickHouse/Typesense recovery path.
- Full still passes its existing authentication, worker messaging, backup, and main media workflow checks after shared code extraction.
- Run backend builds/tests for affected modules and `pnpm run check` for frontend changes. Add focused integration tests for lifecycle, persistence, and migration; do not treat a successful build as proof that consolidation works.
- Record idle memory, startup time, container count, and a representative download/search workload against Full on the same host. Establish the Lite baseline from measurements rather than promising an unmeasured resource reduction.

Completion means both editions are deployable through their init/runtime pairs, Lite fulfills the single-process/no-authentication contract, and Aspire owns every generated Compose/env difference.
