# Phase 1 implementation inventory

Status: implemented, awaiting the runtime verification described in `phase-01-baseline.md`.

Captured: 2026-09-10 on commit `9ebc0ac` (`feature/lite-implementation`). This is the extraction checklist for later FrostStream Lite phases. The source files remain authoritative when code changes.

## Composition roots and ownership

| Process | Current responsibility | Startup evidence | Lite destination |
| --- | --- | --- | --- |
| AppHost | Always creates one Compose environment and provisions NATS, PostgreSQL/init, OpenBao/bootstrap, Typesense, Authentik, OpenFGA/migrate, POT, BackupService, optional ClickHouse, and six application services | [`AppHost.cs`](../../src/App/AppHost/AppHost.cs), [`StartServices.cs`](../../src/App/AppHost/StartServices.cs) | Profiles/publishing in Phases 2–5 and 18 |
| DataBridge | PostgreSQL mappings/migrations, Cleipnir flows, catalog CRUD, imports, discovery, search, chat, user data, worker registry, and most NATS handlers | [`DataBridge/Program.cs`](../../src/App/DataBridge/Program.cs) | Shared operations in Phases 4, 6–7; merged host in Phases 8–13 |
| WebAPI | HTTP API, BFF/authentication, OpenFGA, streaming, casting, backup proxy, and SSE fan-out | [`WebAPI/Program.cs`](../../src/App/WebAPI/Program.cs) | Shared API/fixed owner in Phase 8; media delivery in Phase 12 |
| Worker | Worker registration/heartbeat, yt-dlp, POT shim, downloads, imports, discovery, artifacts, cookies, and file deletion | [`Worker/Program.cs`](../../src/App/Worker/Program.cs) | Executor abstraction in Phase 7; local execution in Phases 10–11 |
| MediaProcessor | ffmpeg/ffprobe audio and stream renditions plus thumbnails | [`MediaProcessor/Program.cs`](../../src/App/MediaProcessor/Program.cs) | Local processing in Phase 12. This code, rather than the stale README description, establishes implemented behavior. |
| Scheduler | Quartz hydration/listener and recurring channel, maintenance, indexing, cleanup, and backup jobs | [`Scheduler/Program.cs`](../../src/App/Scheduler/Program.cs) | Embedded scheduler in Phase 13 |
| BackupService | pgBackRest coordination/verification, stanza startup, REST API, NATS reporting, and token/cookie-gated offline restore UI | [`BackupService/Program.cs`](../../src/App/BackupService/Program.cs) | Backup/command modes in Phases 15 and 17 |
| Frontend | Static Svelte SPA; always fetches `/api/auth/me`, redirects on 401, and exposes account/admin/worker UI | [`+layout.ts`](../../src/App/Frontend/src/routes/+layout.ts), [`+layout.svelte`](../../src/App/Frontend/src/routes/+layout.svelte) | Capability-aware UI and embedded assets in Phase 14 |

No Lite composition may call these `Program.Main` entry points or host the existing executables as child service processes.

## Transport inventory

The rows below cover every registered transport-facing service found in the six composition roots. “Owner” describes how user ownership is carried today. Request/reply paths reply over the NATS message context; command/event paths use core NATS or JetStream according to their publisher/consumer implementation. PostgreSQL is authoritative only where explicitly stated.

| Area and payload family | Producer → consumer | Current durability/owner behavior | Lite replacement and phase |
| --- | --- | --- | --- |
| Sessions and access policies: `UserSession*`, `AccessPolicy*` | WebAPI auth/admin → DataBridge session/policy consumers; reconciliation → OpenFGA | Request/reply; subject/claims identify user; PostgreSQL policy/session state plus OpenFGA tuples | Fixed current-owner direct calls; remove Lite sessions/OpenFGA in Phases 6 and 8. Owner migration in Phase 19 |
| Notifications: preferences, providers, secret updates, test and admin dispatch | WebAPI/Scheduler/DataBridge → `NotificationPreferencesConsumerService` | Request/reply for CRUD/test; dispatch event; owner subject in payload; provider records in PostgreSQL and secrets in OpenBao | Direct application services in Phase 6 and local secrets in Phase 9 |
| Cookies: `CookieProfile*` | WebAPI → DataBridge CRUD → OpenBao; Worker reads/materializes through storage/secret clients | Request/reply; owner ID scopes records and secret paths | Direct owner-scoped service in Phase 6, local store in Phase 9, acquisition wiring in Phase 11 |
| Storage: local/network/S3/Azure/GCS CRUD and `StorageConfigChangedMessage` | WebAPI → `StorageCrudConsumerService`; change publisher → subscribers in Worker/DataBridge | Request/reply CRUD; PostgreSQL configuration, OpenBao credentials; core event is a cache invalidation hint | Direct CRUD plus bounded local invalidation in Phase 6; credentials in Phase 9 |
| Option presets and download config sets | WebAPI → `OptionPresetCrudConsumerService` / `DownloadConfigSetConsumerService` | Request/reply; owner-scoped PostgreSQL records; config includes storage key and worker tag | Direct calls in Phase 6; local-affinity normalization in Phases 11 and 19 |
| Schedules: create/update/get/list/active/overdue/delete/attempt/success/failure and `ScheduleChangedMessage` | WebAPI/Scheduler → DataBridge CRUD; DataBridge → Scheduler listener | Request/reply with PostgreSQL authority; change message is a wake-up; schedule key/global ownership | Direct service and local change signal in Phases 6 and 13 |
| Creator discovery/source CRUD, ignored media, forced queue, discovered batches and asset updates | WebAPI/Scheduler/Worker → `CreatorDiscoveryConsumerService`; Worker discovery handlers execute scans | CRUD/request replies persist source/media records; execution currently routes through workers/tags | Shared operation in Phases 6–7, local acquisition executor in Phase 11, schedules in Phase 13 |
| Download submission: `DownloadRequested`, `DownloadGroupRequested`, playlist expansion | WebAPI/discovery → durable ingress and flow services; DataBridge → Worker/playlist consumers | JetStream ingress with message IDs; Cleipnir/PostgreSQL flow and job records become durable intent | Direct submission that commits durable intent before returning; local executor in Phases 7, 10–11 |
| Download administration: priority/start/stop/group start-stop/lease acquire-renew/circuit clear | WebAPI/Worker → `DownloadAdminConsumerService`; stop-active command → Worker | Request/reply; job/lease/circuit state in PostgreSQL; worker lease/tag routes distributed execution | Direct admin calls; remove distributed lease/tag routing in Phases 7 and 10 |
| Download execution/events: command, progress, stages, completion/failure, group expansion | DataBridge flow → `DownloadCommandsConsumerService`; Worker → DataBridge event/progress consumers | Work command uses JetStream/dedup headers; progress is transient but persisted by DataBridge; job/flow state is durable | Persisted local dispatch/checkpoints plus bounded wake-up/progress in Phases 7 and 10–11 |
| Queue/history/media queries and queue-state event | WebAPI → `DownloadQueueConsumerService`; DataBridge → `DownloadQueueHub` | Queries are core request/reply; PostgreSQL authoritative; state/progress messages are live notification only | Direct query and local event hub with snapshot-on-reconnect in Phases 6 and 10 |
| Background run lifecycle: dispatched/started/progress/completed | Scheduler/Worker/DataBridge/MediaProcessor/BackupService → `BackgroundJobConsumerService` and WebAPI hub | Live NATS reports with DataBridge persistence; UI SSE fans out current updates | Direct reporter backed by persisted run state; bounded SSE in Phases 7 and 10–13/15 |
| Imports: session CRUD/mapping/options/commit/retry/cancel; browse/scan/probe/enrich/execute events | WebAPI/DataBridge dispatcher → Worker import consumers → DataBridge session/event/flow handlers | Session/items and V2 flows in PostgreSQL; worker tag selects executor; messages wake/advance work | Direct session operations and durable local executor in Phases 6–7 and 11 |
| Provider playlists: expand, events, queries, force queue | WebAPI/DataBridge → Worker playlist consumer → DataBridge playlist flow/query consumers | Playlist/job records persist in PostgreSQL; commands/events coordinate expansion | Direct query and durable local expansion in Phases 7 and 11 |
| Catalog metadata, comments, captions, taxonomy, account assets and statistics | WebAPI → DataBridge metadata/search/statistics consumers | Request/reply reads; PostgreSQL authoritative | Typed direct read services in Phase 6; asset execution in Phases 11–12 |
| Search: unified query/similar, sync-upsert, rebuild/reindex | WebAPI/Scheduler/DataBridge events → search consumers/Typesense | Queries are request/reply; Typesense is derived; sync events can be retried from PostgreSQL/source data | Direct query plus idempotent local index work in Phases 6–7 and 12 |
| Media stream resolution: media, thumbnail, caption/list and account assets | WebAPI streaming controllers → `MediaStreamQueryConsumerService` | Request/reply returns storage resolution; authorization/owner claims checked at WebAPI; storage/catalog persistent | Direct read service in Phases 6, 8 and 12 |
| Renditions/thumbnails: resolve, claim, complete, fail, queue, status and progress | WebAPI/DataBridge → MediaProcessor hosted services → DataBridge consumers; progress → WebAPI hub | Queue/status records in PostgreSQL; NATS triggers execution and transient progress | Durable local executor/checkpoints in Phases 7, 10 and 12 |
| Media deletion/transfer | WebAPI/DataBridge → Worker file-delete consumer and DataBridge catalog-delete executor | Catalog/storage references persist; remote file operation is message-driven | Shared operation with recoverable local file executor in Phases 7 and 12 |
| Watch state, likes, user playlists and notes | WebAPI → DataBridge consumers | Request/reply; fixed/authenticated owner ID scopes PostgreSQL records | Direct current-owner calls in Phases 6 and 8 |
| Live chat ingest/backfill/window queries | Worker sidecar/DataBridge/WebAPI → chat consumers | Source sidecars are durable artifacts; ClickHouse holds query data; NATS transports ingest/backfill/query | Local ingest/query with retry-safe checkpoints in Phase 12; backup/rebuild in Phases 15 and 17 |
| Worker registry/heartbeat | Worker → `WorkerRegistryConsumerService`; WebAPI queries registry | Transient heartbeat plus registry view; worker ID/tags select remote capacity | Removed from Lite in Phases 8 and 10; retained in Full |
| POT: local Worker HTTP shim → `PotTunnelRequest` → `PotBrokerConsumerService` → bgutil HTTP | Worker/POT shim → DataBridge broker → provider | Request/reply only; no durable work; failure returns to yt-dlp path | Direct provider call, optional provider container, in Phase 11 |
| Backup commands/status | WebAPI/Scheduler → BackupService internal REST; BackupService publishes background reports to NATS | In-memory backup job store; pgBackRest repository is durable; scheduled idempotency key accepted; restore intentionally starts without NATS | Local coordinator and durable reporting in Phase 15; isolated offline command in Phase 17 |

Transport definitions are centralized under [`Shared/Messaging`](../../src/App/Shared/Messaging). Consumer implementations are under [`DataBridge/Messaging`](../../src/App/DataBridge/Messaging), [`Worker/Services`](../../src/App/Worker/Services), the DataBridge feature folders, WebAPI feature hubs, and [`Scheduler/Services`](../../src/App/Scheduler/Services). When a later phase changes a contract, it must update this inventory or record its replacement evidence.

## Hosted-service inventory

| Host | Registered background work | Assignment |
| --- | --- | --- |
| DataBridge | ClickHouse schema; chat ingest/backfill/query; Typesense startup/sync/search hydration; fixed-owner seed; sessions, notifications, cookies, storage, options, config sets, schedules, discovery, watch state, jobs; Cleipnir flow startup; lease monitor; queue/progress/ingress/events/stage telemetry; imports; playlists; metadata/statistics/stream queries; thumbnails/renditions; delete and access policy | Initialization Phase 4; shared operations Phases 6–7; merged execution Phases 8–13 |
| WebAPI | OpenFGA provision/reconciliation in multi-user mode; download, rendition, and background-run NATS-to-SSE hubs | Fixed-owner/API Phase 8; local event hub Phase 10 |
| Worker | startup registration, heartbeat, POT shim; downloads; import browse/scan/probe/enrich/commit; playlists; discovery/assets; file deletion | Phases 7 and 10–12 |
| MediaProcessor | audio rendition, stream rendition, thumbnail generation | Phases 7 and 12 |
| Scheduler | schedule hydration and schedule-change listener; Quartz owns registered jobs | Phase 13 |
| BackupService | backup coordinator and stanza startup | Phases 4, 15 and 17 |

## HTTP surface inventory

All routes are controller-discovered from [`WebAPI/Features`](../../src/App/WebAPI/Features), plus default health endpoints. The following controller families cover the complete current controller list:

| Surface | Controller families | Lite handling |
| --- | --- | --- |
| Authentication | `api/auth`, browser `auth/login` and `auth/logout` | Compatibility owner/capabilities response or removal of session actions in Phase 8 |
| User data | cookies, notifications, option presets, playlists, notes, watch state, likes | Fixed-owner paths retained in Phases 6, 8 and 9 |
| Library | metadata, statistics, search, playlists, creator monitor | Retained via direct services in Phases 6, 11 and 12 |
| Acquisition/jobs | downloads, queue/progress SSE, config sets, imports, background jobs SSE | Retained in Phases 7, 10 and 11 |
| Media delivery | stream/captions/assets, channel audio, renditions/progress, transfers, casting, live chat | Retained in Phase 12 |
| Operations | schedules, storage, metadata admin, backups | Retained in Phases 9, 13 and 15–17 |
| Full-only management | workers and access-control/users/roles/policies/bundles | Excluded from Lite capabilities/UI in Phases 8 and 14 |

Phase 8 must audit endpoint authorization metadata and claims access; preserving controller discovery alone is insufficient.

## Initialization and recurring startup inventory

| Current behavior | Evidence | Required ownership |
| --- | --- | --- |
| PostgreSQL resource declares FrostStream, Authentik and OpenFGA databases; `postgres-init` creates databases | [`StartPostgres.cs`](../../src/App/AppHost/StartPostgres.cs) | Edition-specific database/init split in Phases 4–5 |
| DataBridge runs all FluentMigrator migrations immediately before starting the host | [`DataBridge/Program.cs`](../../src/App/DataBridge/Program.cs) and [`Migrations/FluentMigrator`](../../src/App/DataBridge/Migrations/FluentMigrator) | Versioned initializer plus compatibility check in Phases 4 and 16 |
| Fixed single-user owner is seeded by a hosted service on ordinary startup | [`SingleUserOwnerSeederService.cs`](../../src/App/DataBridge/Messaging/SingleUserOwnerSeederService.cs) | Explicit initialization in Phases 4 and 16 |
| ClickHouse schema and Typesense collection/startup synchronization run as hosted services | [`DataBridge/Program.cs`](../../src/App/DataBridge/Program.cs) | Init/runtime separation in Phases 4, 12 and 16 |
| Cleipnir flow schemas and persisted flow/job recovery initialize with DataBridge | [`DataBridge/Flows`](../../src/App/DataBridge/Flows), [`DownloadFlowStartupService.cs`](../../src/App/DataBridge/Messaging/DownloadFlowStartupService.cs) | Durable initializer/recovery in Phases 4, 7, 10 and 16 |
| `backup-init` prepares filesystem ownership; BackupService stanza service validates/creates stanza | [`StartPostgres.cs`](../../src/App/AppHost/StartPostgres.cs), [`StanzaStartupService.cs`](../../src/App/BackupService/StanzaStartupService.cs) | Explicit setup then runtime validation in Phases 4–5 and 15–16 |
| `openbao-bootstrap` combines initialization, recovery material, unseal and configuration | [`StartOpenBao.cs`](../../src/App/AppHost/StartOpenBao.cs) | First-time/recurring split in Phase 5 |
| `openfga-migrate` is a one-shot dependency; WebAPI can provision model/tuples | [`StartOpenFga.cs`](../../src/App/AppHost/StartOpenFga.cs), [`WebAPI/Program.cs`](../../src/App/WebAPI/Program.cs) | Full init/runtime split in Phase 5 |
| Authentik server/worker use vendor lifecycle | [`StartAuthentik.cs`](../../src/App/AppHost/StartAuthentik.cs) | Preserve supported behavior in Phases 4–5 |
| Scheduler hydrates active schedules each start; runtime services recover/reconnect | [`ScheduleHydrationService.cs`](../../src/App/Scheduler/Services/ScheduleHydrationService.cs) | Recurring behavior retained in Phases 10 and 13 |

## Persistent data, credentials and affinity

| Item | Current location/handling | Later phase |
| --- | --- | --- |
| PostgreSQL | Named data volume and socket volume; catalog, users, schedules, jobs, flows, storage configuration | 3–5, 7, 10, 16–19 |
| Shared media/assets | `froststream-data` volume for DataBridge/WebAPI/Worker; host path used in run mode | 3, 11–12, 15, 17–19 |
| Typesense | Named volume; API key parameter; index is derived | 3, 12, 15–18 |
| ClickHouse | Optional today, named volume/password; chat source sidecars retained with media | 3, 12, 15–18 |
| OpenBao | Named data volume plus recovery/init material under configured host path; application token passed to services | Full Phase 5; Lite replacement Phase 9; selected-owner migration Phase 19 |
| WebAPI Data Protection | Separate named key volume for BFF cookies | Full retained; Lite secret key ring designed in Phase 9 |
| pgBackRest | Backup host bind, PostgreSQL data/socket mounts; BackupService runs as PostgreSQL uid | 3–5, 15 and 17 |
| Authentik/OpenFGA credentials | Parameters/env plus their PostgreSQL databases | Full only, Phases 3 and 5 |
| Storage credentials/cookies | Metadata in PostgreSQL; values in OpenBao through `ISecretStore`/secret paths | 6, 9, 15, 17, 19 |
| Storage affinity | `storage_key` occurs on configs, jobs, imports, playlists and media editions/content; must remain | 6–7, 11–12, 19 |
| Worker affinity | `worker_tag` on config/import/playlist/job paths; worker registry/leases route work | Remove from Lite execution in Phases 10–11; normalize during Phase 19 |

## Backup coverage gap inventory

Current pgBackRest behavior protects PostgreSQL and its repository. The deployment mounts the shared media volume and OpenBao bootstrap material into backup-related resources, but the current standard backup command does not establish an application-level manifest proving recovery of media, OpenBao/local secrets, ClickHouse, or Typesense. The README explicitly says the OpenBao recovery key is not included in standard backups. Phases 15 and 17 therefore own proof of:

- PostgreSQL catalog/job/schedule state;
- media and archived chat sidecars, including external-storage rules;
- Lite ciphertext and its exact Data Protection key ring;
- ClickHouse irreplaceable data or a tested rebuild from archived sources;
- Typesense rebuild from authoritative data;
- an actual clean restore with application-level checks.

## Phase ownership closure

Every inventory category above is assigned to at least one implementation phase. Cross-cutting publication/configuration belongs to Phases 2–5, 18 and 20; extraction and runtime behavior belongs to Phases 4 and 6–17; migration belongs to Phase 19; final regression, crash testing, and measurement belong to Phase 21.
