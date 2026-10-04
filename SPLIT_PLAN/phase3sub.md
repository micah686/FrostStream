# Phase 3 — Assemble the Lite runtime

This breaks Phase 3 of `SIDE_PLAN.MD` into ordered, reviewable subphases. Phases 1 and 2 provide the shared application modules, contracts, SQLite persistence, workflow store, and restart reconciliation. Keep one implementation of each feature and preserve Full's existing behavior throughout.

## Phase 3a — Local request dispatch and transient notifications

- Implement Lite's shared request-dispatch interfaces using local calls to the existing handlers.
- Use bounded channels for transient notifications, including progress delivery; define capacity, cancellation, and backpressure behavior.
- Keep application handlers independent of NATS and deployment-mode checks.

**Completion:** Shared requests and transient notifications work in-process, including cancellation and progress subscriptions, while Full continues using its existing transport.

**Implementation:** Lite selects `LocalApplicationTransport` for `IMessageBus`, `IRequestDispatcher`, and `IEventBus`. Requests invoke existing subscription handlers directly, including nested requests. Each transient subscription has an ordered bounded channel; `Messaging:Local:SubscriptionCapacity` defaults to 256 and must be positive. Publishers wait for capacity and can cancel that wait. Stopping a subscription releases blocked publishers and discards pending events after the current handler finishes. Request cancellation stops waiting for a reply; the existing handler context does not expose a cancellation token, so it cannot forcibly stop a running handler. Full retains its NATS transport. Durable jobs and staged objects still use the existing adapters until 3b/3c; this subphase alone does not provide an infrastructure-free Lite runtime.

## Phase 3b — Durable SQLite messaging

- Persist durable messages, delivery attempts, leases, and acknowledgments in SQLite.
- Implement transactional outbox/inbox handling and idempotent handlers to tolerate retries and duplicate delivery.
- Connect durable delivery to Phase 2's startup reconciliation so old queue messages cannot restart invalidated download runs.
- Handle concurrent consumers, lease expiry, cancellation, and crash recovery.

**Completion:** Durable jobs survive restarts and retry safely; duplicate delivery and expired leases do not produce duplicate effects or revive invalidated runs.

**Implementation:** With SQLite persistence enabled, Lite selects `SqliteDurableTransport` for durable publishing, consumption, and worker routes. Messages and stable IDs, per-consumer inbox acknowledgments, attempt counts, and fenced leases persist in the application SQLite file. Immediate transactions serialize claims across consumers; expired leases are reclaimed, and stale acknowledgments/heartbeats are rejected. Handler publications form an outbox committed atomically with acknowledgment; negative acknowledgments discard that outbox. Cancellation releases unfinished deliveries after the running handler returns. Shared download handlers retain their existing generation/run/dispatch fences and idempotency checks from Phase 2. Database/file side effects outside the transport transaction must remain idempotent, since a crash before acknowledgment can repeat a handler, as in Full. Staged objects and NATS infrastructure removal remain in 3c.

## Phase 3c — SQLite-backed staged objects

- Replace NATS object storage for import manifests with SQLite-backed storage through the shared object interface.
- Preserve the existing import manifest behavior and lifecycle using shared import handlers.
- Configure Lite without NATS connections, topology services, or the NATS session store.

**Completion:** Imports can stage and retrieve manifests through SQLite, including after restart, and Lite neither registers nor attempts to connect to NATS infrastructure.

**Implementation:** With SQLite persistence enabled, Lite resolves the shared staged-object factory to `SqliteStagedObjectStore`. Migration 3 adds bucket/key-isolated BLOB storage in the application database; writes atomically replace existing objects, reads survive restart, and deletion remains idempotent. Shared import handlers retain their existing manifest staging, retrieval, and cleanup lifecycle. Local transport registration suppresses NATS connections and topology services across modules and no longer registers NATS application adapters as fallbacks. Lite selects the existing single-user authentication branch so external auth configuration cannot register the NATS BFF session store; Admin identity and comprehensive access behavior remain in 3d. Full retains its NATS adapters and configured authentication.

## Phase 3d — Automatic Admin identity and backend access

- Seed the existing stable single-user identity idempotently and present it as **Admin**.
- Use that identity automatically for every Lite request.
- Centralize allow-all behavior across default/fallback policies, endpoint permissions, role checks, and resource access.
- Ensure existing credentials do not trigger external authentication in Lite.
- Preserve Full's existing authentication and authorization protections.

**Completion:** All Lite endpoints and resources are accessible as Admin, including endpoints without permission metadata; repeated startup preserves the same identity, and Full still enforces its configured protections.

**Implementation:** Lite seeds the existing `SingleUserId`/`SingleUserSubject` as **Admin** and awaits that seed before startup reconciliation and consumer initialization. The authentication selector always chooses the local identity in Lite, ignoring stored bearer tokens, browser cookies, and cast/podcast credentials. Lite selects a shared allow-all authorization service and policy provider for default, fallback, named endpoint, role, and direct authorization checks; its policy evaluator also ignores explicit external authentication schemes. Playback, streaming, casting, channel audio, and live-chat consumers share an access-check interface whose Lite implementation permits resource access without transport or group-policy checks. Lite keeps shared controller routes, including access-management routes, and requires no external-auth production opt-in. Full retains its configured authentication, fail-closed fallback, role and endpoint permissions, resource checks, and existing single-user behavior. SQLite restart seeding and HTTP authorization tests cover these boundaries.

## Phase 3e — Frontend access behavior

- Use the shared capabilities endpoint to select Lite's frontend behavior.
- Allow all frontend permission checks in Lite.
- Remove Lite login redirects and access-management controls while keeping normal validation.
- Keep the shared frontend and Full's existing access behavior.

**Completion:** Lite users can navigate and use normal feature flows without login redirects or permission restrictions, and validation and Full's access controls remain functional.

**Implementation:** The shared frontend loads `/api/system/capabilities` before the session and uses its deployment mode to configure centralized permission and login behavior. Lite permits frontend permission decisions, suppresses session/API login redirects and external-auth CSRF/logout requests, presents Admin in the profile, and hides login, sign-out, and access-management controls. Direct access-management URLs (including nested editors and legacy redirects) return to Administration before their controls load. Session failures, API errors, form validation, and feature-state restrictions remain visible and functional. Full retains permission decisions, login return paths, CSRF behavior, and its configured access-management controls. `pnpm test:access` exercises Lite/Full behavior against production load and HTTP modules.

## Phase 3f — Encrypted local secret storage

- Implement `ISecretStore` using encrypted SQLite values.
- Persist the local encryption key ring so secrets remain readable after restart.
- Bind Lite to the local implementation while preserving Full's existing secret-store adapter.
- Keep the key ring available for Phase 4's backup and recovery work.

**Completion:** Shared secret consumers can store and retrieve secrets in Lite without OpenBao, and persisted secrets remain decryptable after restart.

**Implementation:** With SQLite persistence enabled, Lite binds `ISecretStore` to `SqliteSecretStore`; shared storage, cookie, and notification consumers keep their existing interface and secret paths. Migration 4 adds atomically replaced encrypted secret documents to `local_secrets`. A dedicated ASP.NET Core Data Protection provider protects each document with a path-specific purpose, so tampering and ciphertext moved between paths fail authentication. The persistent key ring defaults to `<resolved SQLite database path>.keys`; `Secrets:Local:KeyRingPath` can select a dedicated directory (relative paths resolve against the host content root). On Unix the directory is created with owner-only permissions, and existing directories with group/other access are rejected. `LocalSecretStoreOptions.KeyRingPath` exposes the resolved companion artifact for Phase 4: recovery must preserve the complete key ring alongside the database, including older keys needed after rotation. The local provider is independent of WebAPI cookie/token keys. Lite modules do not register OpenBao; Full retains its existing adapter and configuration. Tests cover restart, rotation, database-plus-key-ring relocation, shared storage hydration, tampering/path isolation, cancellation, concurrent replacement, and idempotent deletion.

## Phase 3g — Scheduler integration

- Reuse existing Scheduler jobs and their trigger/misfire behavior.
- Load schedule definitions from SQLite.
- Route scheduled work through the shared handlers and appropriate local or durable dispatch paths.
- Prepare scheduler registration so execution starts only after startup reconciliation and handler initialization.

**Completion:** SQLite-backed schedules invoke the existing jobs with preserved trigger/misfire behavior, including after restart, without a separate Scheduler process.

**Implementation:** Lite reuses the existing Quartz jobs, registrar, trigger factory, and schedule-change listener. Schedule hydration requests use the local dispatcher and shared schedule handlers/repository against SQLite; job publications use the SQLite durable transport, while schedule status and change notifications use the shared local bus. `SubscriptionBackgroundService.RegistrationCompleted` exposes handler initialization completion. Lite replaces the normal Quartz hosted service with `LiteSchedulerStartupService`, registered after the application modules with sequential hosted-service startup: existing blocking reconciliation completes first, all subscriptions (including schedule changes) become ready, persisted active schedules are hydrated, and only then Quartz starts. Failed hydration prevents scheduling, and shutdown waits for running jobs before handlers stop. Full retains its existing Quartz hosted service and background hydration path. Cron time zones and do-nothing misfires, interval triggers and next-with-remaining-count misfires, job types, and idempotency keys remain shared. SQLite integration tests exercise a real job firing and local status handler, restart hydration, disabled schedules, live create/disable/delete changes, and the startup gate; module tests verify registration order and Full preservation. Local backup client implementation remains in Phase 4.

## Phase 3h — Host assembly and startup ordering

- Assemble DataBridge, MediaProcessor, Scheduler, WebAPI, and Worker modules in the single Lite ASP.NET Core host.
- Explicitly register shared controllers and workflows and avoid duplicate hosted services.
- Enforce initialization order: migrations → admin seed → startup reconciliation → handlers → scheduling and HTTP readiness.
- Select infrastructure implementations at startup rather than adding deployment checks throughout feature code.
- Verify Lite does not require NATS, PostgreSQL, OpenBao, Authentik, OpenFGA, or the PostgreSQL backup service. Keep Typesense, optional ClickHouse, and the POT provider as specified in the overall plan.

**Completion:** Lite starts consistently as one C# process, exposes the shared application routes, and begins serving requests and scheduled work only after required initialization succeeds.

**Implementation:** The dedicated Lite executable defaults to Lite mode and SQLite and rejects incompatible settings. `AddLiteModules` composes all five modules once and shares the existing controllers and workflows. The first hosted-service registration initializes the application and workflow schemas before the host constructs workflow-dependent services; sequential startup then seeds Admin and completes reconciliation. Subscription services, SSE hubs, Worker cancellation handlers, and the optional POT shim expose initialization completion. A shared startup gate holds durable deliveries, import polling, and Worker heartbeats until handler registration succeeds. SQLite schedule hydration and Quartz startup follow that gate, and the final readiness service completes before ASP.NET begins listening. `/health` includes Lite readiness and `/alive` remains available in production. MediaProcessor uses the shared storage provider directly in Lite and defaults to the Worker's provisioned FFmpeg/FFprobe binaries; Full retains its HTTP storage client. Lite selects a local unavailable backup adapter, advertises SQLite backup operations as unavailable, and preserves the shared backup routes without contacting the PostgreSQL backup service; backup implementation remains in Phase 4. Typesense and optional ClickHouse/POT integrations remain unchanged. Real assembled-host tests cover fresh migration, Admin access, reconciliation, delayed and failed initialization, HTTP readiness, Quartz startup/shutdown, adapter selection, and shared route parity; durable-gate and local-storage transfer tests cover the remaining single-process boundaries.


## Phase 3i — End-to-end verification

- Exercise normal download, import, processing, playback, search, scheduling, and progress flows in Lite.
- Verify concurrent writes, duplicate delivery, cancellation, crashes, and restart reconciliation across the assembled runtime.
- Verify automatic Admin access, frontend navigation, and access to new endpoints without permission metadata.
- Run relevant shared API, repository, and workflow checks against Full and Lite; assert identical application routes and preserved Full protections.
- Check that Lite neither deploys nor attempts connections to removed infrastructure.

**Completion:** Lite runs all Phase 3 feature flows without removed infrastructure, and Full retains its existing behavior. Backup implementation, deployment packaging, and release remain in Phase 4.
