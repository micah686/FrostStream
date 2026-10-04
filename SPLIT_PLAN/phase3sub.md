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

## Phase 3f — Encrypted local secret storage

- Implement `ISecretStore` using encrypted SQLite values.
- Persist the local encryption key ring so secrets remain readable after restart.
- Bind Lite to the local implementation while preserving Full's existing secret-store adapter.
- Keep the key ring available for Phase 4's backup and recovery work.

**Completion:** Shared secret consumers can store and retrieve secrets in Lite without OpenBao, and persisted secrets remain decryptable after restart.

## Phase 3g — Scheduler integration

- Reuse existing Scheduler jobs and their trigger/misfire behavior.
- Load schedule definitions from SQLite.
- Route scheduled work through the shared handlers and appropriate local or durable dispatch paths.
- Prepare scheduler registration so execution starts only after startup reconciliation and handler initialization.

**Completion:** SQLite-backed schedules invoke the existing jobs with preserved trigger/misfire behavior, including after restart, without a separate Scheduler process.

## Phase 3h — Host assembly and startup ordering

- Assemble DataBridge, MediaProcessor, Scheduler, WebAPI, and Worker modules in the single Lite ASP.NET Core host.
- Explicitly register shared controllers and workflows and avoid duplicate hosted services.
- Enforce initialization order: migrations → admin seed → startup reconciliation → handlers → scheduling and HTTP readiness.
- Select infrastructure implementations at startup rather than adding deployment checks throughout feature code.
- Verify Lite does not require NATS, PostgreSQL, OpenBao, Authentik, OpenFGA, or the PostgreSQL backup service. Keep Typesense, optional ClickHouse, and the POT provider as specified in the overall plan.

**Completion:** Lite starts consistently as one C# process, exposes the shared application routes, and begins serving requests and scheduled work only after required initialization succeeds.

## Phase 3i — End-to-end verification

- Exercise normal download, import, processing, playback, search, scheduling, and progress flows in Lite.
- Verify concurrent writes, duplicate delivery, cancellation, crashes, and restart reconciliation across the assembled runtime.
- Verify automatic Admin access, frontend navigation, and access to new endpoints without permission metadata.
- Run relevant shared API, repository, and workflow checks against Full and Lite; assert identical application routes and preserved Full protections.
- Check that Lite neither deploys nor attempts connections to removed infrastructure.

**Completion:** Lite runs all Phase 3 feature flows without removed infrastructure, and Full retains its existing behavior. Backup implementation, deployment packaging, and release remain in Phase 4.
