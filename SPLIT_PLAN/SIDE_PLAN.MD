# Full / Lite Refactor

## Summary

Maintain **one implementation of each feature**: shared endpoints, controllers, workflows, jobs, models, and frontend. Deployment mode selects infrastructure adapters and hosting arrangements.

Full retains its complete current feature set. Lite combines DataBridge, MediaProcessor, Scheduler, WebAPI, and Worker into one C# process.

| Concern | Full | Lite |
|---|---|---|
| Hosting | Existing separate services | One ASP.NET Core application |
| Messaging | NATS / JetStream | Local dispatch and SQLite-backed durable queues |
| Database | PostgreSQL | SQLite database named `frostreamlitedb` |
| Authentication | Existing Authentik integration | Automatic local admin identity; no login |
| Authorization | Existing OpenFGA policies | Allow all endpoints and resources |
| Secrets | OpenBao | Encrypted local persistence |
| Backups | Existing PostgreSQL backup service | Built-in SQLite backups |
| Supporting services | Existing services | Keep Typesense, optional ClickHouse, and POT provider |

## Implementation phases

### Phase 1 — Establish shared application modules

- Extract each service’s registrations and application logic into reusable modules. Full’s entry points register their respective modules; a new Lite host registers all five.
- Add `Deployment:Mode=Full|Lite`, defaulting to Full. Select implementations at startup; avoid deployment checks throughout feature code.
- Separate application contracts from NATS types. Introduce shared interfaces for request dispatch, events, durable jobs, and staged objects. Both transports invoke the same handlers.
- Keep one controller assembly and frontend. Add `GET /api/system/capabilities` for available integrations, access-management controls, and backup capabilities.
- Preserve Full’s existing routes, message contracts, configuration compatibility, and behavior throughout extraction.

**Completion:** Full works through the shared modules without regressions.

### Phase 2 — Add SQLite persistence

- Keep shared entities, repositories, and business rules. Isolate provider-specific SQL and mappings behind small adapters.
- Port PostgreSQL schemas, enums, JSON/arrays, timestamps, bulk operations, upserts, and locking. This requires more than replacing `UseNpgsql`.
- Preserve PostgreSQL migration history; introduce a SQLite baseline. Express future schema changes through shared migrations where supported, isolating database-specific operations.
- Use `/data/frostreamlitedb`, with persistent local storage, foreign keys, WAL, bounded write contention, and short transactions.
- Keep Cleipnir workflows shared. Implement a durable SQLite store compatible with the currently pinned workflow library; retain the existing PostgreSQL store for Full.
- Preserve current download restart behavior: queued jobs stop and active jobs become interrupted/failed. Prevent old queue messages from restarting invalidated runs.

**Completion:** Both databases pass the same repository and workflow tests, including upgrades and restart reconciliation.

### Phase 3 — Assemble the Lite runtime

- Dispatch requests locally and use bounded channels for transient notifications. Persist durable messages, delivery attempts, leases, and acknowledgments in SQLite; use transactional outbox/inbox handling and idempotent handlers.
- Replace NATS object storage for import manifests with SQLite-backed storage through the shared object interface. Register no NATS connections, topology services, or session store in Lite.
- Reuse Scheduler jobs and existing trigger/misfire behavior; load schedule definitions from SQLite.
- Seed the existing stable single-user identity idempotently, presenting it as **Admin**. Every request uses that identity automatically.
- Centralize Lite’s allow-all behavior across default/fallback policies, endpoint permissions, role checks, and resource access. Existing credentials must not trigger external authentication. Full retains its current protections.
- Make frontend permission checks allow all in Lite; remove login redirects and access-management controls. Keep normal validation.
- Implement `ISecretStore` using encrypted SQLite values and a persistent local encryption key ring.
- Initialize in order: migrations → admin seed → startup reconciliation → handlers → scheduling and HTTP readiness. Explicitly register shared controllers/workflows and avoid duplicate hosted services.

**Completion:** Lite runs normal download, import, processing, playback, search, scheduling, and progress flows without any removed infrastructure.

### Phase 4 — Backups, packaging, and release

- Reuse existing backup endpoints, UI, scheduling, and `IBackupServiceClient`; bind Full to its HTTP client and Lite to an embedded SQLite implementation.
- Lite supports full snapshots and verification. Hide differential/PITR options; reject unsupported operations consistently through shared handlers. Create snapshots using SQLite’s [backup API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup), publishing files only after successful verification.
- Display the resolved database location and backup directory in the shared backup page. Defaults:
  - Database: `/data/frostreamlitedb`
  - Backups: `/data/backups/<timestamp>-<id>.sqlite`
- Provide restore instructions: **shut down the server, preserve the current database, replace it with the selected backup renamed `frostreamlitedb`, then restart**. Handle leftover WAL/SHM files only after shutdown. Preserve the encryption key ring; include a companion key backup for recovery onto another installation.
- Backups cover application data, workflow/queue state, staged objects, and secrets. Media files and optional ClickHouse data remain separately managed.
- Generate Full and Lite deployment profiles from the same AppHost configuration. Lite packages the C# host, shared frontend, and media tools; excludes NATS, PostgreSQL, OpenBao, Authentik, OpenFGA, and the PostgreSQL backup service.

**Completion:** Both deployment profiles install reproducibly and pass CI.

## Verification and ongoing development

- Run shared API, repository, and workflow tests against both deployments; assert identical application routes.
- Test Lite’s automatic admin access, including new endpoints without permission metadata. Verify Full still enforces its configured authentication and authorization.
- Exercise concurrent writes, duplicate delivery, cancellation, crashes, imports, progress subscriptions, scheduled jobs, and database upgrades.
- Test backup creation during activity, verification failures, and documented shutdown/file-replacement restoration.
- Check that Lite neither deploys nor attempts connections to removed services.
- Require new features to use shared handlers and contracts. Architecture checks prevent feature code from depending directly on deployment adapters.

**Defaults:** Fresh Lite installations first; PostgreSQL-to-SQLite conversion is deferred. Restore uses documented file replacement. Mode changes require restart. Infrastructure adapters and occasional provider-specific migrations require separate maintenance; normal feature development remains shared.
