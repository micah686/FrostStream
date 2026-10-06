# FrostStream Full/Lite architecture review and refactor proposal

**Review baseline:** `5d610cb`  
**Review date:** October 4, 2026  
**Status:** Proposed architecture — for review before application implementation.

## 1. Assessment and recommendation

**Keep the shared application and refactor its infrastructure boundaries.** The existing implementation already contains substantial work worth preserving: reusable backend modules, SQLite persistence, durable workflows, local messaging, encrypted secrets, backups, and deployment tests.

Starting from scratch, I would build a modular application with two hosting arrangements:

- **Lite:** one FrostStream application container plus one POT provider container.
- **Full:** separately deployable application services using external infrastructure.

The current architecture is moving in that direction. The remaining work is larger than changing Compose: search, live chat, authentication, startup sequencing, and messaging need explicit changes.

| Area | Current finding | Assessment |
|---|---|---|
| Application reuse | Lite composes DataBridge, Worker, MediaProcessor, WebAPI, and Scheduler. | Preserve this approach and the shared feature implementations. |
| Lite packaging | Generated Lite deployment includes separate frontend and Typesense containers, plus optional ClickHouse. | Does not meet the requested two-container shape. |
| Search | DataBridge registers Typesense unconditionally; search handlers and the advanced-query parser produce Typesense-specific queries. | Introduce a search boundary before adding SQLite search. |
| Live chat | Ingestion and playback queries directly use ClickHouse. | Lite needs embedded storage behind shared ingestion and query logic. |
| Authentication | Lite automatically authenticates every request as Admin and bypasses authorization. Frontend logic suppresses login redirects. | Replace with authenticated single-user access. |
| SQLite foundation | Provider-specific queries, append-only migrations, workflow persistence, startup reconciliation, secrets, and recoverable backups exist. | Retain and extend; avoid rebuilding these systems. |
| Durable queue | Delivery polling loads candidates before filtering subjects in memory. No message-retention cleanup was found. | Address contention and database growth before adding indexing and chat workloads. |
| Transaction boundaries | The SQLite reply outbox commits with inbox acknowledgment, separately from application-data transactions. | Do not describe it as a complete application transactional outbox. |
| Distributed operation | DataBridge startup reconciles unfinished downloads globally; Quartz uses an in-memory runtime store hydrated from saved schedules. | Support one active DataBridge and Scheduler initially. Queue groups alone do not establish safe replication. |
| Code boundaries | Lite references executable service projects; shared code still carries infrastructure dependencies. Startup relies on registration order and constructor side effects. | Extract reusable libraries and make startup stages explicit. |
| Installation | Documented installation requires profile generation, development tools, and infrastructure credentials even for Lite. | Ship an immediately usable Lite Compose file and prebuilt images. |

Representative evidence includes [Lite composition](../../src/App/LiteApp/Lite/LiteModule.cs), [DataBridge registration](../../src/App/SharedApp/DataBridge/DataBridgeModule.cs), [Lite authorization](../../src/App/SharedApp/WebAPI/Auth/LiteAuthorization.cs), [SQLite durable delivery](../../src/App/SharedApp/DataBridge/Messaging/SqliteDurableTransport.cs), and [download startup reconciliation](../../src/App/SharedApp/DataBridge/Messaging/DownloadFlowStartupService.cs).

The earlier [Full/Lite plan](../../SPLIT_PLAN/SIDE_PLAN.MD) explicitly retained Typesense, optional ClickHouse, and automatic Admin access. Removing those dependencies and requiring login are revised requirements, rather than omissions against that earlier plan.

**Verification performed during the review:** the unit-test project builds with zero warnings or errors; all 39 deployment-focused unit tests pass. Full integration tests, clean container installations, performance, and release CI results were not verified during this review. The findings above combine inspected implementation facts with architectural risk assessments; the performance targets below are proposals, not measured results.

## 2. Target architecture

| Concern | Lite | Full |
|---|---|---|
| Application hosting | One ASP.NET Core process; media tools run as subprocesses | Separate WebAPI, DataBridge, Worker, MediaProcessor, and Scheduler hosts |
| Frontend | Shared static frontend served by FrostStream | Existing separately deployable frontend |
| Application records | One local SQLite database | PostgreSQL |
| Search | SQLite FTS5 and relational queries | Typesense |
| Live-chat archive | SQLite tables | Existing optional ClickHouse integration |
| Requests and transient events | In-process dispatch | NATS |
| Durable jobs | SQLite-backed delivery | JetStream |
| Workflow execution | Shared workflows with existing SQLite store | Shared workflows with PostgreSQL store |
| Identity | One local account requiring login | Existing Authentik/OIDC integration |
| Permissions | Authenticated local owner | OpenFGA |
| Secrets | Encrypted SQLite values and persistent local keys | OpenBao |
| Media | Files or configured storage targets | Shared network storage or object storage for cross-host deployments |
| Backups | Embedded database snapshots plus key companions | Existing PostgreSQL/OpenBao backup service |
| Supporting containers | POT provider only | Existing infrastructure services |

Lite stores application records, accounts, sessions, encrypted secrets, jobs, schedules, search indexes, and chat records in SQLite. Media, sidecars, temporary files, and encryption keys remain outside the database in persistent storage.

The SQLite database must reside on local storage. WAL permits concurrent readers but only one writer, and is unsuitable for a database shared across hosts over a network filesystem. [SQLite WAL documentation](https://www.sqlite.org/wal.html)

### Code structure

Use three layers beneath thin executable hosts:

- **Contracts and feature modules:** application behavior, workflows, validation, DTOs, and infrastructure interfaces.
- **Infrastructure adapters:** PostgreSQL/SQLite, NATS/local dispatch, Typesense/FTS5, ClickHouse/SQLite chat, OpenBao/local secrets, and identity/session implementations.
- **Composition roots:** select compatible adapters and orchestrate startup.

Keep one implementation of each feature. Provider-specific queries and migrations remain explicit; do not build a universal database or messaging abstraction.

Retain Cleipnir, Quartz, the existing repositories, and the established job/run/lease identifiers. Replacing those systems would add migration risk without being necessary to achieve the deployment goals.

## 3. Staged refactor

### Phase 1 — Establish explicit composition and ownership

- Extract reusable modules from executable projects into libraries. Full hosts register their respective modules; Lite registers all modules.
- Move infrastructure registrations and dependencies out of feature libraries. The final Lite dependency graph should not require Full-only service clients.
- Resolve `Deployment:Mode` once into a validated deployment profile. Separate deployment topology, identity provider, single-user ownership, and feature availability.
- Replace schema work in service constructors and hosted-service list manipulation with an explicit sequence: schema initialization → owner initialization → download reconciliation → handler readiness → scheduling → HTTP readiness.
- Keep workflow watchdogs gated until reconciliation completes.
- Preserve existing configuration names where practical. Reject incompatible combinations with actionable startup errors.

**Completion:** both existing deployments behave as before through the extracted modules, with architectural checks preventing new infrastructure dependencies in feature code.

### Phase 2 — Complete the SQLite messaging foundation

- Introduce a provider-neutral durable-route catalog containing subscription, delivery, retry, and retention policies. Both SQLite and JetStream adapters consume it.
- Replace broad queue scans with persisted delivery routing and indexed, bounded claims. Use notification-assisted wakeups with bounded polling for recovery.
- Preserve leases, renewal, fencing, acknowledgment, idempotency, cancellation, and existing restart behavior.
- Add bounded retries and recorded terminal failures using the existing route policies.
- Clean completed payloads and inbox records safely. Preserve deduplication markers separately for the supported replay period and never discard active work.
- Add a transactional application outbox for durable consequences of database changes, particularly job dispatch and search-index updates. Write outbox records in the same application transaction; dispatch after commit with stable message IDs.
- Keep progress notifications transient. Do not turn every UI update into durable work.

**Completion:** crashes between database commit, publication, processing, and acknowledgment cannot silently lose required work or duplicate committed effects.

### Phase 3 — Replace Lite search and chat infrastructure

#### Search

Introduce `ISearchService` and `ISearchIndexer`. Shared handlers depend on these interfaces; provider adapters own query translation and index operations.

- Change the advanced-query parser to produce typed filters, rather than Typesense filter strings.
- Preserve current routes, search scopes, structured filters, sorting, pagination fields, caption/comment lookup, private-note behavior, and similar-media navigation.
- Use SQL for ordinary browsing and structured filtering; use FTS5 for text matching and weighted relevance. FTS5 provides prefix queries, BM25 ranking, and snippet support. [SQLite FTS5 documentation](https://www.sqlite.org/fts5.html)
- Keep indexes derived from canonical application records and stored sidecars. Feed updates through the durable outbox, with idempotent indexing and resumable rebuilds.
- Expose rebuild progress and temporary search unavailability explicitly. Library browsing and playback remain usable during rebuilds.
- Document differences in tokenization, ranking, and typo tolerance. Exact Typesense relevance is outside the agreed scope.

#### Live chat

Introduce `ILiveChatStore` for ingestion batches, playback windows, deletion, and backfill.

- Share parsing, normalization, emote handling, and API responses.
- Add SQLite storage indexed by media, playback offset, and message identity.
- Use bounded ingestion batches and an ingestion generation so an incomplete replacement does not become the visible archive.
- Preserve window limits, ordering, deduplication, seek behavior, and restart recovery.
- Enable chat availability in Lite without requiring a ClickHouse connection.

**Completion:** search and chat workflows operate with no Typesense or ClickHouse container or connection attempt.

### Phase 4 — Add authenticated single-user Lite

Introduce a local identity provider and SQLite-backed session storage while retaining the existing owner subject. Existing media ownership and preferences must remain attached to that identity.

- Provide first-run account setup protected by a single-use token obtained through a local container command.
- Use ASP.NET Core password hashing and cookie authentication with opaque, revocable server-side sessions. Keep persistent protection keys. [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0)
- Provide login, logout, password change, and local-command recovery. No registration, email delivery, or external identity service is required.
- Require authentication before granting owner access. Remove unconditional backend success policies and automatic identity injection.
- Retain CSRF protection, login throttling, cookie protections, and safe return-path validation.
- Preserve scoped casting and podcast access; those mechanisms must not become unrestricted owner sessions.
- Change frontend behavior to use authentication capabilities rather than assuming that Lite means “no login.”

#### Public interface changes

- Extend `/api/system/capabilities` additively with authentication provider, login requirement, and setup state. Preserve existing capability fields.
- Keep `/api/auth/me` and the existing session/profile contract.
- Retain `/auth/login` as the browser entry point: local login for Lite, OIDC for Full.
- Add Lite setup, credential login, password change, and recovery operations.
- Route logout through a session abstraction so Lite does not depend on `NatsBffTicketStore`.
- Protect newly added feature endpoints by default; only explicitly public setup/login, capability, health, and scoped-token surfaces bypass ordinary session authentication.

**Completion:** unauthenticated requests cannot access private application data, while the authenticated owner can use all supported Lite features.

### Phase 5 — Deliver the deployment shapes

#### Lite

- Build the existing static Svelte frontend into the Lite image and serve it from ASP.NET Core. Preserve SPA deep links, immutable-asset caching, `/stream` routing, streaming responses, and authentication paths. [ASP.NET Core static files](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0)
- Publish exactly two services: `froststream` and `pot-provider`.
- Keep yt-dlp, FFmpeg, FFprobe, Deno, and the matching POT plugin packaged at image-build time.
- Default the published port to localhost; document explicit LAN binding and HTTPS configuration.
- Publish versioned amd64/arm64 images and a ready-to-run Compose file. End users need a container runtime, not .NET, Aspire, Python, or frontend build tools.
- Preserve existing database, backup, and key paths. Do not rename the existing database merely to correct its spelling.
- POT provider outages should report download failures without preventing library browsing or playback.

#### Full

- Retain separate service hosts, external dependencies, and existing API/message contracts.
- Support explicit cross-host service addresses, credentials, TLS, storage configuration, and worker-local scratch paths.
- Enforce one active DataBridge and Scheduler using PostgreSQL advisory locks held for their lifetimes. Acquire ownership before reconciliation or scheduling; shut down on ownership loss.
- Support multiple workers and independently placed media processors. Multiple active API/control-plane instances and automatic failover remain outside this release.
- Add an explicit service authentication policy for MediaProcessor’s HTTP storage transfers. The current client supplies no service credentials, while the controller inherits Full’s restrictive fallback policy. Use an OIDC service client with an internal-storage audience and read/write scopes.
- Validate cross-host storage access. Host-local volumes cannot serve as shared media storage across machines.
- Correct outdated deployment and scaling documentation.

**Completion:** Lite installs as two containers; Full completes a download and playback workflow with workers/processors on a different host from the control services.

### Phase 6 — Preserve upgrades and release evidence

- Append migrations to existing SQLite and PostgreSQL histories; preserve released migration checksums and workflow serialization.
- Back up data and keys before upgrade. Rollback uses the preserved database, keys, configuration, and matching prior image.
- Rebuild SQLite search from canonical data before retiring the old Typesense service. Retain old volumes until verification completes.
- For existing Lite installations using ClickHouse, backfill SQLite from retained sidecars. Provide a one-time import utility for records whose sidecars are unavailable. Verify imported coverage before removing the dependency.
- Existing Lite installations enter protected account setup without changing owner identifiers or losing settings.
- Extend backup verification to cover local credentials, restored records, chat, and search integrity. Invalidate restored browser sessions.
- Update profile validators and runtime tests that currently require Typesense and a frontend container in Lite.
- Publish matching operating guides and release artifacts only after the complete installation matrix succeeds.

**Completion:** existing Full and Lite installations upgrade without losing application data; deployment-mode conversion remains deferred.

## 4. Acceptance and verification

| Area | Required evidence |
|---|---|
| Container shape | Lite starts from fresh storage using exactly FrostStream and POT provider, including search and chat. |
| Dependency isolation | Runtime canaries detect zero Lite connection attempts to removed infrastructure. |
| Feature preservation | Shared scenarios cover downloads, imports, metadata, playback, captions, comments, search, notes, schedules, notifications, backups, casting, and podcasts. |
| Authentication | Test setup races, anonymous access, login/logout, expiration, password changes, session revocation, CSRF, recovery, and scoped media tokens. |
| Search | Compare supported fields, operators, filters, sorting, and response contracts across providers; test index updates, deletes, rebuild interruption, and private-note isolation. |
| Chat | Test duplicate ingestion, interrupted replacement, backfill, seeks, ordering, bounded windows, deletion, and upgrade import. |
| Reliability | Kill processes around commit/publication/acknowledgment; verify deduplication, fencing, lease recovery, and preserved explicit-retry behavior. |
| Queue maintenance | Large completed histories must not cause unbounded polling scans; retention must preserve pending delivery and replay protection. |
| Upgrades and recovery | Upgrade representative existing databases; restore snapshots and complete key rings into another installation; verify credentials and application records. |
| Full distribution | Exercise two hosts, remote media storage, service authentication, network interruptions, worker scaling, and rejection of duplicate control-service instances. |
| Packaging | Install prebuilt images on Linux amd64 and arm64; verify frontend deep links, range playback, streaming progress, and packaged media tools. |

Use a repeatable home-library benchmark containing **10,000 media items and one million records each for captions, comments, and chat**, on a documented 4-core, 8-GB machine with local SSD storage. Run it with two download jobs, one transcode, and online backup activity.

Initial release targets are **p95 search under one second**, **p95 bounded chat-window reads under 250 milliseconds**, and no lost writes or unhandled database-lock failures. These are proposed acceptance targets, not measured claims. Record database size, process memory, queue latency, writer contention, and rebuild duration alongside results.

## 5. Review decisions and document deliverables

The following decisions are fixed by this review:

- Preserve everyday feature coverage in Lite.
- Require one local login account.
- Preserve existing Full and Lite data through upgrades.
- Permit documented search-ranking and typo-tolerance differences.
- Keep media and encryption keys outside SQLite.
- Preserve current download restart semantics: queued downloads stop; active downloads become interrupted; retry is explicit.
- Support distributed placement and worker scaling before control-plane high availability.
- Defer automated Lite-to-Full and Full-to-Lite conversion.
- Prioritize an ordinary home-library workload.

The review is delivered in two equivalent documents:

- **`full-lite-review.md`** — canonical Markdown containing the assessment, evidence, architecture, phases, and acceptance criteria.
- **`full-lite-review.html`** — standalone styled rendering of the same content, with section navigation, a deployment comparison, findings table, and print layout.

The HTML uses embedded CSS, readable typography, responsive tables, and accessible contrast. It works offline without external fonts, scripts, or diagram services. Source-code links resolve within this checkout; external reference links require an internet connection. Both versions distinguish observed facts, proposed changes, and unverified targets.
