# FrostStream Lite implementation phases

Status: active implementation plan. Phase 1 is complete with its recorded runtime limitation; Phase 2 is accepted; Phase 3 is verified and awaiting acceptance. Later phases remain unimplemented.

Source of truth: [FROSTREAM_LITE_PLAN.md](FROSTREAM_LITE_PLAN.md). This document divides that plan into actionable, sequential work blocks without changing its scope. Proposed files and command interfaces below become concrete during their owning phase.

## How to use this document

Ask: **“Implement Phase 7 from FROSTSTREAM_LITE_PHASES.MD, run its verification, and stop for review.”**

- Implement only the requested phase and its necessary fixes. Check its prerequisites first; report any unmet prerequisite rather than silently implementing additional phases.
- Work in numeric order. Every phase requires the preceding phase's gate to have passed and been accepted; additional technical dependencies are noted where useful.
- At the start, inspect current code and applicable repository guidance. Reuse work already present only after verifying it. Generated `obj`/`bin` artifacts do not establish that a feature exists.
- Each phase must leave a buildable, reviewable state. Until the complete Lite deployment passes Phase 18, describe it as a development milestone rather than a deployable edition. Full must remain functional throughout.
- Add focused tests for changed behavior and failure paths. Use disposable installations, synthetic credentials, and controlled media fixtures for lifecycle, crash, and restore tests.
- At completion, record changed files, exact commands, test results, manual reproduction steps and expected results, known limitations, and a rollback procedure. A build alone does not satisfy a runtime gate.
- If required infrastructure, credentials, or casting hardware is unavailable, record the unverified check explicitly. Do not mark the phase verified until its required evidence is available.
- Stop after the phase report so the user can verify it before requesting the next phase. Do not automatically advance.

Suggested phase states: **Not started → In progress → Implemented, awaiting verification → Verified, awaiting acceptance → Accepted**. Keep a dated evidence record per phase under a proposed `docs/lite/` directory; do not commit populated secret files or recovery material.

## Requirements that apply throughout

1. Full retains its distributed services, authentication, authorization, and OpenBao support. Lite has one ASP.NET Core process and one persistent .NET container, with no application login or remote workers.
2. Lite always provisions PostgreSQL, ClickHouse, and Typesense. An explicitly enabled bgutil POT provider is the only optional fifth runtime container in the baseline. Chat ingestion can be disabled without removing ClickHouse.
3. Preserve downloads, creator subscriptions, playlists, imports, multiple storage targets, media editions, search, playback, casting, chat, processing, schedules, preferences, and backups.
4. Aspire owns all Compose/env differences. Publishing must not patch, rename, parse-and-rewrite, or merge generated artifacts. Read-only parsing for verification is allowed.
5. Init bundles are complete first-start stacks. Normal bundles contain no init jobs or references to them. Init/runtime pairs reuse installation identity, credentials, volumes, networks, and host paths.
6. PostgreSQL owns durable work. In-memory channels only deliver bounded wake-ups/progress. Lite must reject concurrent execution instances sharing its database.
7. Use the existing fixed owner ID, encrypted persistent local secrets, and a persistent key ring. Backup and restore must preserve both secrets and keys.
8. Schema-changing upgrades and edition migration are explicit operations. Do not destroy volumes during init/runtime handoff or modify the original Full installation during migration.

### Exact deployment profile names

| External profile / output directory | Edition | Initialization included | Default Compose project |
| --- | --- | --- | --- |
| `frostream-full-init` | Full | Yes | `froststream-full` |
| `froststream-full` | Full | No | `froststream-full` |
| `frostream-lite-init` | Lite | Yes | `froststream-lite` |
| `frostream-lite` | Lite | No | `froststream-lite` |

Preserve these exact spellings. Internal namespaces use `FrostStream`. Installation-name overrides must apply identically to both profiles in a pair.

## Phase 1 — Capture the implementation inventory and Full baseline

**State (2026-09-10): Complete, with a blocked Full runtime verification.** Inventory, build/test results, runtime attempt, partial resource snapshot, prerequisites, and the repeatable smoke procedure are recorded under [`docs/lite/`](docs/lite/). SSH.NET correctly resolves to 2026.0.0 after restoring stale assets. The existing Full graph cannot pass its successful one-shot dependency under Podman Compose 1.6.0, so the application smoke/workload remains explicitly blocked until compatible Docker Compose is used or Phase 5 fixes lifecycle portability.

**Outcome:** A concrete inventory and reproducible baseline against which extraction and consolidation can be checked.

**Work block**

- [x] Inspect the six backend entry points, AppHost factories, frontend startup/auth flows, existing tests, and generation/startup scripts.
- [x] Inventory every request/reply, JetStream command, event consumer, progress stream, workflow ingress, schedule notification, POT call, and backup report. Record producer, consumer, payload, owner handling, persistence/acknowledgment semantics, and proposed Lite replacement.
- [x] Inventory schema/setup work, hosted services, HTTP routes, internal credentials, persistent paths, storage affinities, and backup coverage. Assign each item to a later phase; no unassigned paths remain.
- [x] Record .NET, Aspire SDK/package/CLI, Node/pnpm, database, yt-dlp, ffmpeg, and pgBackRest versions actually in use. Identify prerequisites for running existing tests.
- [x] Capture Full build/test results and a smoke scenario covering login, worker download, ingest, search, playback, and backup. Record pre-existing failures separately.
- [x] Capture a reproducible Full resource baseline attempt: host configuration, static/live partial container count, startup timing/failure, image/runtime versions, and partial memory are recorded. The representative download/search workload could not run because Podman Compose rejects the exited-zero `backup-init` dependency; this is retained as blocked evidence rather than reported as a pass.

**Verification gate:** Inventory entries point to implementation files, baseline commands can be repeated, and failures are distinguished from missing prerequisites. Do not infer MediaProcessor functionality from the outdated README.

**Review checkpoint:** Deliver the inventory, baseline evidence, and the concrete smoke-test procedure. No runtime architecture change is required in this phase.

## Phase 2 — Prove native four-profile Aspire publishing

**State (2026-09-10): Accepted.** The isolated fixture, pinned 13.5.3 toolchain, four native publish/prepare outputs, graph assertions, Compose validation, and byte-identical alternate publication are recorded in [`docs/lite/phase-02-compose-publishing.md`](docs/lite/phase-02-compose-publishing.md). Production topology is unchanged apart from aligning its AppHost SDK with the already centralized 13.5.3 packages.

**Outcome:** A small isolated fixture proves the publisher behavior before production topology is refactored.

**Primary areas:** AppHost version configuration; proposed publishing fixture and profile model.

**Work block**

- [x] Align and pin a verified compatible Aspire SDK/package/CLI toolchain; preserve the existing AppHost build.
- [x] Introduce/test immutable profile selection with explicit name, edition, initialization flag, and separate developer-tools option. Reject unknown profiles before constructing resources.
- [x] Use a fresh resource graph per selected profile. In the fixture, exercise a runtime resource, conditional init job, parameter, persistent volume, and conditional dependency.
- [x] Prove native Compose/env emission into four separate directories with native filenames, dashboard exclusion, and matching installation identity within pairs.
- [x] Prove the distinction between placeholder env publication and resolved-value preparation using the pinned version. Record exact working commands/APIs and how noninteractive inputs are supplied.
- [x] Verify publication to an alternate output directory and stable repeated generation. Record any native metadata differences.

**Verification gate:** All four fixture bundles pass Compose configuration validation. Read-only graph assertions establish correct membership, valid references, pair identity, and expected parameter resolution. No generated artifact edits are used to pass.

**Review checkpoint:** Provide the fixture invocation and resulting sanitized graphs. This is a publisher proof, not a claim that the production Lite stack exists.

## Phase 3 — Add production profile selection and installation configuration

**State (2026-09-11): Verified, awaiting acceptance.** Production AppHost profile resolution, installation-scoped configuration, preserved resolved inputs, typed Compose integration, Full publication, alternate-path validation, and limitations are recorded in [`docs/lite/phase-03-production-profiles.md`](docs/lite/phase-03-production-profiles.md). Lite profiles fail before resource construction because their production graph is intentionally deferred to Phase 8; the Full init/runtime lifecycle split remains Phase 5 work.

**Outcome:** Production AppHost composition is driven by explicit profiles and stable installation inputs.

**Primary areas:** `AppHost.cs`; proposed `DeploymentProfiles.cs` and `ComposePublishing.cs`; configuration loading and resource factories.

**Work block**

- [x] Resolve the profile before profile-dependent configuration and resource registration. Build only its edition's graph; keep local development tooling a separate option.
- [x] Centralize project, volume, network, image, port, build-context, and persistent-host-path naming. Support an installation-name override and separate Full/Lite defaults.
- [x] Share resolved deployment inputs between an edition's init/runtime profiles so passwords and keys cannot be independently regenerated.
- [x] Apply input precedence: explicit publish inputs/environment, then profile defaults. Load development env values only in local run mode or through an explicitly selected file.
- [x] Define ignored resolved deployment output and sanitized templates. Preserve existing installation values when regenerating templates; keep live configuration separate from disposable publish output.
- [x] Integrate the proven typed Compose/env callbacks. Do not yet replace the public generation scripts or advertise incomplete Lite profiles as usable deployments.

**Verification gate:** Tests prove profile selection, configuration precedence, pair naming/credential identity, independent-installation isolation, and path resolution in default/alternate destinations. Invalid configuration produces edition-specific errors without exposing secrets.

**Review checkpoint:** Show sanitized Full output and configuration tests. Clearly identify any Lite runtime resource still awaiting implementation.

## Phase 4 — Extract explicit initialization from recurring startup

**Outcome:** Application setup has a versioned initializer and a separate read-only startup compatibility check.

**Primary areas:** DataBridge migrations/startup, owner seeding, search/chat setup, Cleipnir setup, PostgreSQL and backup initialization.

**Work block**

- [ ] Turn Phase 1's lifecycle inventory into reusable initialization steps with dependencies, finite deadlines, failure reporting, and safe retries.
- [ ] Move application migrations, fixed-owner seeding where applicable, ClickHouse schema, Typesense collections, required workflow tables, and backup stanza setup behind explicit initialization entry points.
- [ ] Record durable initialization/schema compatibility only after all required steps succeed. Account for partial success across services; retries must reconcile it safely.
- [ ] Add ordinary-startup validation for required databases, schema/version compatibility, collections, directories, and stanza. Preserve reconnects, schedule hydration, workflow recovery, and incremental indexing as runtime duties.
- [ ] Expose an initializer that Full can execute now and the merged Lite image can invoke later. Retain supported Authentik lifecycle behavior; do not invent an unsupported standalone migration job.
- [ ] Wire the transitional Full deployment to call explicit setup so extraction does not break existing first-start behavior before Phase 5 finishes the split.

**Verification gate:** Empty-state initialization succeeds; injected mid-initialization failure records no overall success; retry creates no duplicate owner or changed credentials. Normal startup on empty/incompatible state fails with the matching init-profile instruction. An already initialized restart does not run migrations/seeding.

**Review checkpoint:** Demonstrate init, failed init, retry, and runtime validation against disposable state.

## Phase 5 — Complete Full init/runtime lifecycle and OpenBao restart

**Outcome:** Both Full bundles operate independently with the correct lifecycle responsibilities.

**Primary areas:** `StartPostgres.cs`, `StartOpenBao.cs`, `StartOpenFga.cs`, `StartBackupService.cs`, service dependencies and resource records.

**Work block**

- [ ] Make required database lists and init resources profile-specific. Include directory preparation, PostgreSQL setup, application setup, OpenFGA migration, and first-time OpenBao work only where applicable.
- [ ] Separate OpenBao first-time initialization/policy/recovery-material creation from recurring unseal behavior. Implement and document the supported runtime unseal path and signal/readiness handling.
- [ ] Make omitted init resources optional in dependency helpers. Remove dangling dependencies, endpoint expressions, env references, and mounts from normal output.
- [ ] Give init jobs `restart: "no"`, finite retry deadlines, nonzero failure exits, and successful-completion dependencies. Runtime uses real infrastructure health and compatibility checks.
- [ ] Publish both Full bundles using native callbacks, without dashboards or database/NATS developer tooling by default.

**Verification gate:** On disposable volumes, Full init succeeds and seeded data survives handoff to normal under the same project. Remove completed init containers and recreate all runtime containers; Full becomes ready and OpenBao unseals. Failed init blocks dependent application readiness. Full login, authorization, worker messaging, media workflow, and backup still work.

**Review checkpoint:** Provide exact first-start/handoff/recreation commands. Never use `down -v` for handoff.

## Phase 6 — Extract shared catalog and API application operations

**Outcome:** Synchronous business operations can be called without NATS while Full keeps its existing transport contracts.

**Primary areas:** DataBridge consumers/repositories, WebAPI services/controllers, Shared contracts; proposed reusable backend modules.

**Work block**

- [ ] Extract typed async application interfaces for catalog, library, storage, preferences, playlists, watch state, notes, notifications, cookie profiles, and related synchronous operations in the inventory.
- [ ] Move business logic into reusable services; keep NATS serialization, routing, and replies in Full adapters.
- [ ] Separate reusable dependency registration from executable startup. Keep scoped database lifetimes and cancellation propagation correct.
- [ ] Introduce the current-owner abstraction: Full resolves its authenticated identity; the Lite implementation will resolve the established fixed owner ID.
- [ ] Preserve public contracts and error semantics. Update the inventory with migrated paths and remaining transport dependencies.

**Verification gate:** Direct-call and Full-adapter tests produce equivalent results for representative success, validation, ownership, and failure cases. Existing affected API/storage/owner tests pass and the Full smoke path still works.

**Review checkpoint:** Show one operation exercised through both adapters and the updated inventory. No existing `Program.Main` method is invoked as a module.

## Phase 7 — Extract durable workflows and local executor interfaces

**Outcome:** Long-running work has explicit transport-independent contracts and recovery semantics.

**Primary areas:** DataBridge Cleipnir flows/job records, Worker and MediaProcessor handlers, Scheduler dispatch, backup reporting, progress hubs.

**Work block**

- [ ] Extract workflow/executor interfaces for downloads, creator scans, playlist expansion, imports, media processing, search/chat work, schedules, and backups.
- [ ] Keep Full NATS adapters calling shared operations. Separate business completion from transport acknowledgment.
- [ ] Specify each job category's durable intent, checkpoints, retry policy, cancellation, deduplication key, artifact reconciliation, and restart behavior.
- [ ] Preserve PostgreSQL-backed Cleipnir state and persisted job records. Identify any schema additions needed for local execution and add them to explicit initialization.
- [ ] Define bounded wake-up/progress interfaces and persisted snapshot recovery for SSE. Do not use an in-memory queue as the only record of accepted work.

**Verification gate:** Tests establish commit-before-acknowledgment, retry/cancellation semantics, and duplicate-delivery behavior for extracted operations. Full workflow contract/regression tests still pass. Every inventory path now has an assigned direct-call, durable-work, or local-event destination.

**Review checkpoint:** Deliver the per-job recovery matrix and demonstrate a persisted job surviving service reconstruction.

## Phase 8 — Build the merged Lite host with fixed-owner API access

**Outcome:** A minimal Lite backend runs catalog/API operations without an identity provider or broker.

**Primary areas:** Proposed `src/App/FrostStream.Lite/`, reusable module registration, WebAPI route discovery/auth policies, production validation.

**Work block**

- [ ] Create one ASP.NET Core entry point with explicit command dispatch boundaries for future `initialize`, `backup`, and `restore` modes. Register only the runtime modules currently implemented.
- [ ] Consolidate DI, options, logging, HTTP clients, route/controller discovery, readiness, and graceful shutdown. Detect duplicate hosted-service registration and route collisions.
- [ ] Resolve all user-owned operations to the existing fixed owner ID. Audit claims assumptions, authorization filters/attributes, and internal processing credentials.
- [ ] Register no Lite OIDC/JWT/cookie authentication, session store, authentication challenge, OpenFGA client, or user provisioning. Add an edition/capabilities response and, if needed, sessionless `/api/auth/me` compatibility.
- [ ] Make Lite Production validation accept absent identity/secrets infrastructure settings while retaining required database/search/storage credentials and Full validation.
- [ ] Run against explicitly initialized development infrastructure. Disable dispatch capabilities that are not yet implemented instead of falsely reporting them as ready.

**Verification gate:** Unauthenticated HTTP requests can perform owner-scoped catalog/preferences operations with no token, session cookie, or provider call. Two client sessions see the same owner. Full authentication behavior remains intact; DI/startup tests confirm no NATS or Full auth service is activated in Lite.

**Review checkpoint:** Provide a reproducible backend launch and HTTP smoke checks. Download/runtime deployment completion remains a later gate.

## Phase 9 — Implement encrypted local secrets

**Outcome:** Lite storage credentials and cookies persist securely through the shared secret abstraction.

**Primary areas:** `ISecretStore`, `SecretPaths`, `StorageSecretSplitter`, Lite DI/options and persistent directories.

**Work block**

- [ ] Implement encrypted file-backed reads/writes/deletes using Data Protection with a persisted key ring and stable application name/purpose.
- [ ] Preserve secret path/splitting semantics; enforce bounded, path-safe mapping, serialized writes, atomic replacement, restrictive permissions, and redacted diagnostics.
- [ ] Distinguish fresh installation from existing ciphertext with missing/unusable keys. Fail with a clear recovery instruction rather than silently replacing credentials.
- [ ] Connect credentialed storage and cookie-profile services to the local store. Keep Full's OpenBao implementation selected in Full.
- [ ] Define the secret/key backup manifest and selected-owner migration interface for later phases. Document the limits of storing keys beside ciphertext.

**Verification gate:** Round-trip, overwrite/delete, concurrent-write, malformed-path, interrupted-write, missing-key, and restart tests pass. On-disk secret payloads are encrypted; logs/examples contain no real credentials. A credentialed storage operation and cookie materialization work with the local store.

**Review checkpoint:** Demonstrate restart persistence and missing-key recovery errors with synthetic secrets.

## Phase 10 — Add exclusive local execution and durable dispatch

**Outcome:** Lite can safely dispatch persisted jobs with bounded concurrency and restart recovery.

**Primary areas:** Lite runtime lifecycle, PostgreSQL ownership lock, shared job/workflow services, local event hub.

**Work block**

- [ ] Hold an exclusive PostgreSQL advisory lock on a dedicated connection before enabling execution; reject a second Lite runtime using the database.
- [ ] On lock/connection loss, stop dispatch and cancel active execution before any reacquisition/recovery. Prevent stale executors from committing after ownership is lost.
- [ ] Implement local executor dispatch over durable job/workflow records, bounded wake-ups, and configurable bounded concurrency; default to one download at a time.
- [ ] Recover persisted pending/running work at startup using Phase 7's policies. Implement cancellation, retry, and idempotent completion boundaries.
- [ ] Add bounded local SSE subscribers with slow-client handling and an authoritative persisted snapshot on reconnect.
- [ ] Exclude remote worker registration, heartbeat, queue groups, tag routing, and distributed lease monitoring from Lite execution.

**Verification gate:** A persisted test job survives a process kill; duplicate wake-ups cause one logical completion. Concurrency stays bounded. A second process is rejected. Deliberate lock-connection loss cancels execution and prevents stale completion, and SSE reconnect obtains current persisted state.

**Review checkpoint:** Deliver repeatable crash/lock-loss scenarios and results. Use a controlled executor fixture before enabling real media work.

## Phase 11 — Connect downloads, discovery, imports, and POT support

**Outcome:** Lite's main acquisition workflows run locally without service-to-service transport.

**Primary areas:** Worker operations, download/discovery/import flows, cookie materialization, artifact storage, POT integration.

**Work block**

- [ ] Wire yt-dlp execution, creator subscriptions/scans, playlist expansion, and imports to local executor interfaces.
- [ ] Preserve option presets, cookies, metadata enrichment, media editions, multiple storage targets, and storage validation.
- [ ] Replace the NATS POT broker/shim path with direct provider/application calls. Support an explicitly configured bgutil provider and actionable provider-failure reporting.
- [ ] Reconcile temporary/final artifacts and sidecars across cancellation, retry, process death, and catalog commit boundaries.
- [ ] Keep persisted status/progress and local SSE aligned with durable job state; update capability availability and the transport inventory.

**Verification gate:** Controlled download → artifact storage → catalog ingest works with progress, cancel, and retry. Creator scan, playlist expansion, local import, cookie-backed acquisition, and credentialed storage pass. Kill/restart during download and artifact commit yields no duplicate catalog commit or permanently stuck job. Exercise POT enabled, disabled, and unavailable cases.

**Review checkpoint:** Provide fixtures, acquisition steps, persisted evidence, and any external-provider test prerequisites.

## Phase 12 — Connect media processing, search, chat, and delivery

**Outcome:** Acquired media is processed, searchable, playable, and castable with chat support.

**Primary areas:** MediaProcessor modules, search/chat services, WebAPI streaming/casting, Lite HTTP/event plumbing.

**Work block**

- [ ] Wire rendition, thumbnail, ffmpeg/ffprobe, and derived-artifact work to local durable executors.
- [ ] Connect Typesense indexing/query services and ClickHouse chat ingestion/replay; retain archived chat source artifacts where used for reconstruction.
- [ ] Preserve retry-safe indexing/chat checkpoints and recovery of partially created derived artifacts.
- [ ] Verify media range requests, streaming/SSE behavior, subtitle/related delivery routes, casting URLs, and removal of obsolete internal service credentials.
- [ ] Keep ClickHouse required while making ingestion independently configurable. Remove remaining transport dependencies for these paths.

**Verification gate:** Download → process → search → playback passes, including a rendition and thumbnail. Chat ingests/replays and retries without duplicate logical data. Kill/restart during indexing and processing recovers correctly. HTTP range/cancel behavior and a real casting target pass with the configured public URL.

**Review checkpoint:** Supply an end-to-end media fixture and browser/device steps, with persistence and crash evidence.

## Phase 13 — Embed scheduling and recurring background work

**Outcome:** Lite schedules survive restart and dispatch through the local execution owner.

**Primary areas:** Scheduler/Quartz registration, schedule-change notifications, creator monitoring, recurring maintenance and backup scheduling hooks.

**Work block**

- [ ] Register Quartz in the merged process and replace NATS scheduling notifications/dispatch with shared services and local signals.
- [ ] Hydrate persisted schedules before enabling dispatch. Define misfire, retry, cancellation, and deduplication behavior using the job recovery matrix.
- [ ] Connect creator scans, maintenance, and other recurring tasks. Register backup schedule integration without claiming backup completion before Phase 15.
- [ ] Ensure lock loss and shutdown stop scheduler dispatch and cancel local execution consistently.

**Verification gate:** Create/update/delete schedules, restart, and confirm expected next runs. Kill during execution and verify one logical result with recoverable job state. A second Lite instance or lost ownership never continues scheduled execution. Full Scheduler behavior remains intact.

**Review checkpoint:** Provide a short-interval fixture schedule and a deterministic restart/misfire reproduction.

## Phase 14 — Embed the frontend and build the Lite image

**Outcome:** Browser users access Lite through one application container and encounter no login or remote-worker flows.

**Primary areas:** Lite Dockerfile/static hosting, Frontend `+layout.ts`, capability-driven UI, frontend build configuration.

**Work block**

- [ ] Build the static SPA in a Node/pnpm build stage and copy it into the merged app's static assets.
- [ ] Use edition capabilities to skip Lite login/session refresh and hide logout, users, roles, policies, and worker management. Preserve preferences and all supported media features.
- [ ] Restrict SPA fallback to frontend navigation; preserve API errors, media responses, SSE streaming, and health endpoint semantics.
- [ ] Preserve the public frontend port and casting URL configuration. Include yt-dlp dependencies, ffmpeg/ffprobe, and required backup tools in the runtime image.
- [ ] Verify process/container composition: one .NET host, with allowed media/backup subprocesses; no invocation of existing executable entry points or separate frontend server.

**Verification gate:** `pnpm run check` and `pnpm run build` pass. The built image serves deep links and the media workflow without login/token/session-cookie prerequisites. Unknown API routes do not return SPA HTML. Full frontend login and management views still work. Runtime tools report the expected versions.

**Review checkpoint:** Provide the image build/run commands and browser checks for Lite and Full.

## Phase 15 — Integrate backups and the backup command

**Outcome:** Lite creates verifiable backups from its merged image and embedded coordinator.

**Primary areas:** BackupService operations, Lite `backup` command, scheduler integration, pgBackRest configuration/mounts.

**Work block**

- [ ] Move backup coordination into reusable services and invoke them locally; remove Lite internal HTTP/NATS backup calls.
- [ ] Implement `backup` mode and scheduled backup execution with shared durable reporting and safe coordination with the running application.
- [ ] Retain compatible PostgreSQL/pgBackRest tools, data/socket access, configuration, ownership, and stanza validation.
- [ ] Define a consistent backup manifest covering PostgreSQL, media, encrypted secrets/key ring, chat source/ClickHouse recovery, and Typesense reconstruction. Explicitly identify external media handling and any data not included.
- [ ] Remove OpenBao pairing from Lite manifests while retaining Full behavior. Include a supported backup of irreplaceable chat data if source artifacts cannot reconstruct it.

**Verification gate:** Manual and scheduled backup succeed on a populated disposable installation; manifest/content checks confirm required data and both secrets/key material. Inject a backup failure and verify truthful durable status and safe retry. Full backup regression checks pass.

**Review checkpoint:** Provide backup invocation, schedule demonstration, and coverage manifest. A successful backup command alone does not establish recoverability; Phase 17 proves restore.

## Phase 16 — Finish Lite initialization and normal startup validation

**Outcome:** The merged image explicitly initializes all Lite state; normal mode validates compatibility and recovers runtime work.

**Primary areas:** Lite `initialize` command, shared initializer from Phase 4, readiness/options, edition-specific setup.

**Work block**

- [ ] Wire `initialize` mode to Lite-only database creation/setup, application migrations, fixed owner, ClickHouse, Typesense, workflow state, persistent-directory preparation, and backup stanza requirements.
- [ ] Keep initialization DI isolated from normal web/workflow/scheduler startup. Enforce coordination so schema-changing initialization cannot race active execution.
- [ ] Finalize versioned success recording, finite failures, retry behavior, and compatible-schema checks across all modules added since Phase 4.
- [ ] Ensure normal startup validates writable mounts and secret/key integrity, then hydrates schedules and recovers workflows before declaring readiness.
- [ ] Produce exact matching init-profile instructions for empty or upgrade-required state without silently migrating on ordinary restart.

**Verification gate:** Fresh initialization, repeated initialization, partial failure/retry, and a migration-required upgrade fixture pass. Seeds and credentials remain stable. Normal startup rejects empty/incompatible state and starts compatible state without setup mutations; recurring recovery still runs.

**Review checkpoint:** Deliver merged-image initialization commands and explicit expected exit codes/readiness behavior.

## Phase 17 — Implement and prove offline restore

**Outcome:** The same Lite image restores a real backup while ordinary application and PostgreSQL services are stopped.

**Primary areas:** Lite `restore` command, BackupService restore operations, manifest validation and recovery documentation.

**Work block**

- [ ] Implement command-local restore startup that does not wait for PostgreSQL or start web, workflow, scheduler, or normal readiness services.
- [ ] Replace the Lite token-gated restore-server workflow with explicit local command inputs. Validate backup/version compatibility, target paths, ownership, and stopped-runtime requirements before modifying the target.
- [ ] Restore PostgreSQL, media coverage, ciphertext and matching keys, and the declared ClickHouse recovery data. Rebuild Typesense through the supported explicit recovery procedure.
- [ ] Define partial-restore failure handling and retry instructions. Preserve the backup source and document what target data restoration replaces.
- [ ] Restore into a disposable clean installation, then start normal services and verify the recovered application.

**Verification gate:** Restore starts with PostgreSQL unavailable and no normal host services activated. After restoration, catalog/media links, storage credentials, cookies, watch state, schedules, chat replay, and search work. Missing-key, incompatible-backup, and interrupted-restore cases fail clearly and can follow the documented recovery path.

**Review checkpoint:** Provide the complete backup → stop → restore → restart drill and observed recovered records/files.

## Phase 18 — Publish and validate both complete Lite bundles

**Outcome:** Lite is deployable through its complete init/runtime pair.

**Primary areas:** Proposed `StartLiteServices.cs`, AppHost profile factories, Compose callbacks, image/mount/health configuration.

**Work block**

- [ ] Register the merged backend, PostgreSQL, ClickHouse, Typesense, and only the explicitly configured optional POT provider. Add required init jobs using the merged image where applicable.
- [ ] Exclude NATS, OpenBao, Authentik, OpenFGA, separate backend/frontend executables, and developer tooling from Lite deployment output and parameters.
- [ ] Ensure the normal bundle omits all init services/references; init is a complete stack using successful-completion conditions.
- [ ] Verify pair-identical runtime service definitions except explicitly permitted init dependency differences, including images, ports, mounts, volumes, networks, and resolved credentials.
- [ ] Exercise absolute/shared persistent paths and an alternate publish directory. Demonstrate that changing bundle directories does not change installation storage.

**Verification gate:** Both bundles pass Compose config/graph checks. Baseline Lite has exactly four long-running containers, or five with POT explicitly enabled, and one persistent .NET service. Empty init, intentional init failure/retry, handoff, and full runtime recreation pass with preserved catalog/media/secrets/chat/search state. No init containers are needed for normal restart.

**Review checkpoint:** Provide exact deployment commands and a working browser/media/backup smoke test on the published pair.

## Phase 19 — Implement offline Full-to-Lite migration

**Outcome:** One selected Full owner can move to a separate Lite installation with a preserved Full rollback source.

**Primary areas:** Proposed migration commands/tooling, owner mapping, local-secret import/export, data/storage/workflow reconciliation.

**Work block**

- [ ] Define a versioned export/import manifest, source/target compatibility checks, explicit owner selection, and a preflight report of data/work affected.
- [ ] Require distributed work to be drained/canceled or explicitly reconciled. Do not mark in-flight NATS work completed merely because transport is removed.
- [ ] Export only the selected owner's secrets through the Full secret service, excluding root tokens and unseal material; import through the Lite encrypted store without leaking plaintext into logs/config examples.
- [ ] Map owner foreign keys and secret paths to the existing fixed Lite owner. Preserve preferences, watch state, playlists, notifications, cookies, media editions, storage targets, and media references.
- [ ] Normalize worker/storage affinity to the local executor; retain legacy columns where needed. Reject ambiguous multi-user merges and unsupported references instead of dropping records silently.
- [ ] Rebuild derived indexes and apply the documented chat recovery route. Leave original Full data/secrets usable for rollback; define partial-import cleanup/retry against a separate target.

**Verification gate:** A multi-user fixture exports only the selected owner's permitted data/secrets, imports into fresh Lite, and passes search/playback/cookies/storage/watch-state checks. Pending-work and ambiguous-owner cases are handled explicitly. Failed import can be retried safely, and the untouched source Full installation starts and remains usable.

**Review checkpoint:** Deliver migration/preflight commands, before/after record checks, rejection cases, and rollback instructions.

## Phase 20 — Replace generation scripts and document operation

**Outcome:** One supported cross-platform command generates all four real bundles, and operators can follow complete lifecycle instructions.

**Primary areas:** Proposed .NET/MSBuild publish-all entry point, `generateCompose.sh`, `generateCompose.ps1`, startup helpers, README and sanitized examples.

**Work block**

- [ ] Promote the proven native publishing orchestration to a cross-platform entry point with explicit output directories and profile/installation inputs. One invocation produces all four bundles.
- [ ] Remove shell/PowerShell YAML/env transformations, artifact renaming, generated-file merging, and development-secret copying. Retained launchers only delegate to the publisher.
- [ ] Preserve native `docker-compose.yaml` and `.env` filenames beneath the exact four named directories. Document resolved-value preparation and keep live secrets in ignored output.
- [ ] Verify regeneration preserves user-managed installation values and does not silently rotate credentials. Update old startup helpers and stale deployment references.
- [ ] Document prerequisites, first start, init/runtime handoff, normal restart, upgrades, partial-init recovery, POT configuration, backup coverage, offline restore, migration, and rollback.
- [ ] Include installation-name overrides, stable host paths, exact profile spellings, and the distinction between no application login and required infrastructure credentials.

**Verification gate:** Run the documented publish-all command on supported Windows and Unix environments (or equivalent CI coverage), with default and alternate output destinations. All four bundles validate; repeat generation is stable apart from documented metadata and preserves live values. Follow the operational guide from a clean disposable installation through normal restart and restore.

**Review checkpoint:** Provide the final exact command, artifact listing, cross-platform evidence, and operator walkthrough. Do not claim platform verification from code inspection alone.

## Phase 21 — Run the release acceptance matrix and measure Lite

**Outcome:** The original plan's acceptance criteria are evidenced across both editions.

**Work block**

- [ ] Run the complete artifact/lifecycle matrix across all four production bundles, including optional POT, alternate output paths, installation isolation, stable regeneration, failure/retry, upgrade, and container recreation.
- [ ] Run Lite's full feature journey: download, ingest, search, playback, SSE, cancel/retry, creator scans, playlists, imports, renditions, casting, cookies, credentialed storage, and chat replay.
- [ ] Repeat crash injection during download, artifact commit, indexing, and schedule execution; test second-instance rejection and ownership-lock loss with real workflows.
- [ ] Repeat local-secret persistence, missing-key behavior, backup/offline restore, and selected-owner migration drills on release artifacts.
- [ ] Run Full authentication/authorization, worker messaging, media workflow, and backup regression checks after all shared extraction is complete.
- [ ] Run affected backend builds/tests and frontend checks. Close every inventory entry with implementation and test evidence; remove any temporary development-only capability stubs from supported paths.
- [ ] Measure Lite and Full on the same host with the Phase 1 workload: idle memory, readiness/startup time, long-running container count, download/search behavior, versions, dataset, and concurrency. Report measurements without inventing a savings target.
- [ ] Consolidate phase evidence and record remaining operational limitations. Fix acceptance failures within scope and rerun the failed checks before declaring completion.

**Verification gate:** Every acceptance item in the source plan has a passing result and reproduction reference. There are no unresolved mandatory checks, data-loss/recovery failures, hidden Full dependencies in Lite, or regressions masked by a successful build.

**Review checkpoint:** Present the acceptance matrix and resource comparison for user review. Completion means both editions are deployable through their init/runtime pairs, Lite meets the single-process/no-login contract, and Aspire owns every Compose/env difference.

## Verification command conventions

Run commands from the repository root unless a working directory is stated. These existing project paths provide starting points; Phase 1 must confirm test-runner invocation, prerequisites, and useful focused filters. Record the exact command used in each phase rather than claiming these examples were run when writing this document.

```bash
dotnet build src/App/FrostStream.slnx
dotnet run --project Tests/UnitTests/UnitTests.csproj -- --no-ansi --disable-logo
dotnet run --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- --no-ansi --disable-logo
```

For frontend changes, from `src/App/Frontend`:

```bash
pnpm run check
pnpm run build
```

For each generated bundle, using its actual resolved env file:

```bash
docker compose --env-file <bundle-directory>/.env -f <bundle-directory>/docker-compose.yaml config --quiet
```

Publishing syntax is deliberately finalized by Phase 2 and exposed as the supported entry point in Phase 20. Do not assume a CLI environment flag selects the resource topology. Compose configuration validation must be supplemented by read-only graph assertions and runtime exercises.

## Source-plan coverage map

| Source-plan segment | Owning phases |
| --- | --- |
| Repository inventory and baseline | 1 |
| Native Compose proof, profiles, parameters, identity and paths | 2–3, 18, 20 |
| Explicit initialization and Full lifecycle/OpenBao restart | 4–5 |
| Shared backend operations and transport replacement | 6–7, 10–13 |
| Merged host, fixed owner, no authentication | 8, 14 |
| Local encrypted secrets and persistent keys | 9, 15, 17, 19 |
| Durable execution, bounded progress, crash/lock recovery | 7, 10–13, 21 |
| Downloads, discovery, imports, storage and POT | 11 |
| Processing, search, chat, playback and casting | 12 |
| Embedded scheduling | 13 |
| Static SPA, image dependencies and frontend capabilities | 14 |
| Backup, Lite initialization, offline restore | 15–17 |
| Full-to-Lite migration and rollback | 19 |
| Cross-platform publishing and operational documentation | 20 |
| All artifact/application acceptance checks and measurements | 21 |
