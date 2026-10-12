# FrostStream Architecture Review and Phased Refactor

Status: **Architecture proposal. The application refactor described here is not implemented by creating this document.**

Reviewed: 2026-10-10. Audience: engineers implementing the Lite and Full deployments.

## 1. Recommendation

Keep one implementation of FrostStream's application features, organized into reusable class-library modules. Compose those modules into one Lite backend process or several Full service processes through separate, thin executable hosts.

The current application already has much of the required behavior. Retain its controllers, repositories, workflows, media processing, storage integrations, and frontend. Refactor the boundaries around them: process startup, infrastructure registration, search, authentication, persistence providers, and durable work.

The desired shared base is a set of focused libraries, not a new catch-all `Shared` project. Lite and Full are deployment compositions, not forks of the product. Full still needs multiple executable hosts even though it is one edition.

The immediate deliverable is this repository-root `REARCH_PLAN.MD`. Each implementation phase below is subsequent work that can be undertaken separately.

### 0. IMPORTANT:
All development will take place in this src-refactor folder. While you may read files from ../src/, the final files will need to be here. Assume I will be deleting the old ../src/ at the end of this excerise, so no referring to a library or similar that is staying in ../src/
I have already copied the old src/tests/... into src-refactor/reference/src. this is to be your REFERENCE only. Any of the new arefactor needs to go over in src-refactor/new_refactor_src. 
remember, the new refactor should structure should look like this (in the src-refactor/new_refactor_src/):
```text
Shared/
  Contracts/
  Core/
  Persistence/
  Integrations/
  Modules/
    Api/
    DataBridge/
    Worker/
    MediaProcessor/
    Scheduler/

Lite/
  Infrastructure/
  Backend/
  AppHost/

Full/
  Infrastructure/
  Security/
  Hosts/
    WebAPI/
    DataBridge/
    Worker/
    MediaProcessor/
    Scheduler/
    BackupService/
  AppHost/

Frontend/
```

### Confirmed requirements

| Decision | Target |
| --- | --- |
| Installation compatibility | Fresh installations only. No existing data, account, or configuration migration is required. Breaking changes are acceptable. |
| Refactor scope | Reuse application behavior and code; avoid an application or frontend rewrite. |
| Lite hardware | An 8 GB laptop, with conservative background-work defaults. |
| Lite backend | One .NET application containing API, DataBridge, Worker, MediaProcessor, and Scheduler functionality. |
| Lite supporting containers | Separate frontend, POT provider, and optional ClickHouse. SQLite and Lucene are embedded, not containers. |
| Lite identity | One local Admin identity; no multiple accounts, registration, external login, or account switching. |
| Lite network exposure | Published frontend bound to loopback by default; trusted-LAN access requires an explicit opt-in. |
| Full identity | Authentik-based multi-user authentication only, with OpenFGA authorization. Remove Full single-user mode. |
| Full infrastructure | Preserve Typesense, NATS, PostgreSQL, OpenBao, Authentik, OpenFGA, BackupService, and separate application services. |
| Lite search | Embedded Lucene.Net with a separate rebuildable index directory. |
| Search parity | Same user-facing search capabilities; provider-specific relevance ranking may differ. |
| Phase cutovers | Short maintenance windows and recreating development/test databases are acceptable. |

The separate Lucene directory supersedes the initial suggestion of a second SQLite search database. Search remains physically separate from authoritative application data without introducing a second database or a custom Lucene storage layer.

### Non-goals

- Rewriting the Svelte frontend, every controller, or every repository.
- Replacing EF Core, the workflow engine, yt-dlp, FFmpeg, or existing storage integrations as part of this refactor.
- Adding another broker, more production microservices, event sourcing, or a generic repository framework.
- Converting existing installations, preserving old configuration aliases, or supporting mixed-version rolling upgrades.
- Claiming that search performance or memory use has improved before measurement.

## 2. Current architecture review

These findings come from static inspection of the current repository. They are not load-test results or evidence that every listed risk is an observed production failure.

### What is already useful

- Lite already has its own executable and Aspire AppHost, and combines the same application modules used by Full.
- Application contracts separate request/event dispatch and durable jobs from their implementations.
- Local dispatch, SQLite workflows, SQLite durable transport, encrypted local secrets, and SQLite recovery already exist.
- Repository operations already have explicit PostgreSQL and SQLite variants.
- The frontend already consumes a capabilities endpoint and shares its pages between editions.
- Runtime tests already exercise real Lite persistence, indexing, media processing, and interrupted-job behavior.

These are foundations to retain, not systems to replace wholesale.

### Findings and recommended corrections

| Priority | Finding and evidence | Consequence | Correction |
| --- | --- | --- | --- |
| High | [Lite.csproj](src/App/LiteApp/Lite/Lite.csproj) references the executable service projects and removes competing appsettings during build/publish. | Reusable functionality and process packaging are coupled. | Extract class libraries and give each executable ownership of its own settings and entry point. |
| High | [Shared.csproj](src/App/SharedApp/Shared/Shared.csproj) includes Conduit.NATS and VaultSharp; DataBridge carries both database providers and Typesense. | Runtime registration suppression does not provide dependency isolation for Lite. | Move provider implementations and SDK dependencies into infrastructure projects. |
| High | [LiteModule.cs](src/App/LiteApp/Lite/LiteModule.cs) removes/reinserts scheduler services; [LiteStartupServices.cs](src/App/LiteApp/Lite/LiteStartupServices.cs) initializes schema in a constructor. | Correctness depends on DI registration and construction order. | Introduce explicit initialization and readiness stages; keep constructors free of I/O. |
| High | [ModuleRegistration.cs](src/App/SharedApp/Shared/Deployment/ModuleRegistration.cs) suppresses NATS registration through markers while individual modules still configure NATS. | Modules choose process infrastructure that should belong to hosts. | Select providers once in the composition root, then register modules against those interfaces. |
| High | [MetadataListConsumerService.cs](src/App/SharedApp/DataBridge/Search/MetadataListConsumerService.cs), unified search, captions, and other consumers directly use Typesense. | Replacing only `/api/search` would leave Lite library browsing dependent on Typesense. | Introduce a search boundary covering all search-backed surfaces. |
| High | [AdvancedQueryParser.cs](src/App/SharedApp/DataBridge/Search/AdvancedQueryParser.cs) emits Typesense filter clauses. | User query parsing cannot be reused by a different search engine cleanly. | Parse into a typed query model and translate separately per provider. |
| High | [TypesenseSyncConsumerService.cs](src/App/SharedApp/DataBridge/Search/TypesenseSyncConsumerService.cs) consumes transient messages and logs indexing errors without durable retry. | A committed record change can be missing from the index until another update or rebuild. | Persist indexing intent with the record mutation and retry idempotently. |
| High | [SqliteDurableTransport.cs](src/App/SharedApp/DataBridge/Messaging/SqliteDurableTransport.cs) polls every 100 ms, materializes candidate messages before subject filtering, and enters a write transaction to discover work. No pruning path was found for its message/inbox tables. | Idle consumers and growing history can add avoidable SQLite contention, allocations, and disk growth. | Bounded route-aware claims, wake-up signals, backoff, and explicit retention. |
| Medium | [AuthMode.cs](src/App/SharedApp/Shared/Auth/AuthMode.cs) permits Full single-user mode through flags. | There are more identity modes than the requested product supports. | Make edition identity behavior explicit and reject obsolete Full single-user settings. |
| Medium | [PersistenceRegistration.cs](src/App/SharedApp/DataBridge/Persistence/PersistenceRegistration.cs) also registers secrets, backups, staged storage, durable transport, and workflows. | A persistence registration method owns unrelated runtime responsibilities. | Split registrations by responsibility and compose them in the host. |
| Medium | [MetadataRebuildCoordinator.cs](src/App/SharedApp/DataBridge/Search/MetadataRebuildCoordinator.cs) recreates active collections before repopulating them. | Rebuild failures can leave search unavailable or incomplete. | Build replacement generations and activate only after validation/catch-up. |

The highest-value work is improving composition, search independence, reliable index maintenance, and the Lite queue. A large directory rename without these changes would provide little architectural benefit.

## 3. Target projects and dependency rules

### Logical layout

```text
Shared/
  Contracts/
  Core/
  Persistence/
  Integrations/
  Modules/
    Api/
    DataBridge/
    Worker/
    MediaProcessor/
    Scheduler/

Lite/
  Infrastructure/
  Backend/
  AppHost/

Full/
  Infrastructure/
  Security/
  Hosts/
    WebAPI/
    DataBridge/
    Worker/
    MediaProcessor/
    Scheduler/
    BackupService/
  AppHost/

Frontend/
```

This is the target logical structure, not a requirement to move every file in the first phase. Convert existing projects where possible. Keep namespaces and assembly/resource identities stable during mechanical extraction when it reduces unrelated compiler and test churn; rename only after the new boundaries work.

### Responsibilities

| Project/group | Owns | Must not own |
| --- | --- | --- |
| Contracts | Request/reply messages, DTOs, events, infrastructure ports, and provider-independent job route definitions. | Host configuration, vendor clients, concrete repositories, DI provider selection. |
| Core | Shared application concepts, validation, policies, and value types. | A grab bag of adapters, deployment flags, or service startup. |
| Persistence | Shared EF model/configuration, repositories, database operations, and provider extension points. | Concrete PostgreSQL/SQLite connection setup, external infrastructure, or edition selection. |
| Integrations | Reusable media-tool and storage implementations, plus configured optional live-chat integration. | Full identity/security or process composition. |
| Modules | Existing feature behavior and handlers organized by current service responsibility. | Selecting a database, transport, search engine, or authentication edition. |
| Lite.Infrastructure | SQLite provider and workflows, local dispatcher/queue, local secrets, snapshots, local identity, and Lucene. | Full-only SDKs or contacting removed services. |
| Full.Infrastructure | PostgreSQL, NATS, Typesense, OpenBao, remote service clients, and backup integration. | Registering every Full dependency for every process indiscriminately. |
| Full.Security | Authentik/BFF/OpenFGA implementation and Full-only account/access-management endpoints. | Lite identity or authentication fallbacks. |
| Hosts/AppHosts | Entry points, options binding, provider registration, lifecycle, endpoints, health, and deployment wiring. | Business rules or duplicate implementations of application features. |
| Frontend | One UI using stable application APIs and capabilities. | Direct access to infrastructure or inferring providers from session mode. |

Maintain a small number of projects aligned with actual dependency boundaries. An adapter family can start as a folder inside its infrastructure project. Extract another project when it is necessary to prevent a forbidden transitive dependency or support independent composition, not merely because a class has an interface.

### Allowed dependency direction

- Hosts reference modules and the required infrastructure/integrations.
- Modules use Contracts/Core, plus shared Persistence where existing data operations belong.
- Infrastructure implements ports declared in shared contracts or module boundaries; modules never reference those implementations.
- Integrations implement ports; hosts register them. Do not create a cycle by making a module reference an integration that references that same module.
- Contracts/Core have no references to executable projects or vendor infrastructure SDKs.
- No executable references another executable.
- Full.Security is used by the Full WebAPI host only; other Full services receive trusted request context through application contracts.
- Provider-neutral Persistence must not require Npgsql, SQLite, Typesense, NATS, or VaultSharp to compile. Move provider model configuration and dialect selection behind explicit extensions.
- Lite's final transitive dependency graph excludes NATS, PostgreSQL, Typesense, VaultSharp, Authentik, and OpenFGA implementations.

Keep the shared EF context and operation-specific repositories. There is no need to redesign every entity into a new domain model or replace explicit SQL with a generic abstraction. Database entities/configuration move into Persistence; shared message DTOs should not expose EF tracking or provider types.

### Transitional dependency exception

Before embedded search is ready, both editions may continue using Typesense behind the new search interface. Keep that adapter in an isolated library so temporarily referencing it from Lite does not pull in the entire Full infrastructure/security graph. Record this as the single temporary search dependency exception and remove it during Phase 5. The final architecture has no Lite Typesense dependency.

## 4. Deployment and data ownership

| Responsibility | Lite | Full |
| --- | --- | --- |
| API/data/workers/media/scheduling | One backend process | Separate service processes |
| Frontend | Separate container | Separate container |
| Application database | SQLite file | PostgreSQL |
| Request/reply | In-process dispatcher | NATS |
| Transient events | Bounded in-process dispatch | NATS core messaging |
| Durable work | SQLite queue and workflow store | JetStream and PostgreSQL workflow store |
| Search | Embedded Lucene index | Typesense |
| Secrets | Encrypted local values and persistent key ring | OpenBao |
| Account authentication | Fixed local Admin | Authentik |
| Authorization | Local Admin policy | OpenFGA |
| Backups | In-process SQLite recovery sets | BackupService/pgBackRest with vault pairing |
| POT provider | Container | Container |
| Live-chat archive | Optional ClickHouse | Optional ClickHouse |

Lite normally has three containers: backend, frontend, and POT provider. Enabling live-chat archival adds ClickHouse. SQLite and Lucene run inside the backend and use persistent storage; they are not additional services.

The POT provider remains part of the YouTube acquisition integration. Cookie profiles and their secrets remain owned by the application secret-store path rather than being conflated with the POT provider.

### Full service responsibilities

- WebAPI owns the HTTP boundary, browser sessions, authentication/authorization, and frontend event connections.
- DataBridge owns application records, orchestration, repository mutations, and search projection coordination.
- Worker owns acquisition/import execution and worker-local storage access.
- MediaProcessor owns encoding and media-derived artifacts.
- Scheduler owns scheduling triggers; it invokes application operations through contracts.
- BackupService remains an independently reachable recovery service, including when the rest of Full is stopped.

Retain one DataBridge/control-plane instance initially and preserve independent Worker/MediaProcessor scaling. Do not add direct database access to other services just because Lite co-locates them. Lite composes the same responsibility boundaries in one process.

### Lite storage layout

| Location | Contents | Recovery classification |
| --- | --- | --- |
| `/data/froststream.db` | Application records, workflow/queue state, encrypted secret values, and pending-index work. | Authoritative; include in application snapshots. |
| `/data/keys/` | Complete persistent encryption key ring. | Required with encrypted data; retain all needed keys. |
| `/data/backups/` | Snapshot recovery sets and manifests. | Recovery artifacts. |
| `/search/` | Lucene index generations, schema metadata, and derived checkpoints. | Rebuildable; separate volume/mount from authoritative data. |
| Configured media storage | Downloaded/imported media and retained source sidecars. | Authoritative media; separate backup policy. |
| Configured incoming/temp roots | Import input and bounded processing scratch space. | Classify retained input separately from disposable scratch files. |

Do not create a second SQLite database just for authoritative queue/workflow state. Keeping authoritative state together supports consistent recovery and transactional writes. It does not automatically make independently opened transactions atomic; operations that must commit together must explicitly share the transaction.

Search can be deleted and rebuilt from application records and retained source files. It is excluded from mandatory snapshots. Database backups do not imply that media bytes or ClickHouse archives are included; their backup responsibilities must be documented separately.

## 5. Embedded search and the common search boundary

### Engine choice

Use Lucene.Net directly behind FrostStream interfaces for the initial Lite implementation. This gives explicit control of query translation, buffering, commit completion, backpressure, and rebuild activation. Examine is a reasonable alternative wrapper, but is not needed in the initial design and will not be added as a second parallel abstraction.

Examine wraps Lucene.Net and documents asynchronous indexing behavior; Lucene's normal filesystem directory and writer APIs provide the storage/lifecycle required here. Keep both libraries' types out of application contracts. [Examine overview](https://shazwazza.github.io/Examine/), [Examine indexing](https://shazwazza.github.io/Examine/articles/indexing.html), [Lucene filesystem directory](https://lucenenet.apache.org/docs/4.8.0-beta00017/api/core/Lucene.Net.Store.FSDirectory.html), [Lucene writer](https://lucenenet.apache.org/docs/4.8.0-beta00017/api/core/Lucene.Net.Index.IndexWriter.html).

Lucene.Net's published line uses prerelease version labels. Pin a verified compatible package set, including required analysis/query/highlighting packages, and run .NET 10/Linux architecture checks before integrating it. Do not infer production suitability or performance solely from the package label. [Apache Lucene.Net project](https://lucenenet.apache.org/).

SQLite FTS5 was considered because it offers a separate SQLite full-text database, but the selected requirement is richer embedded Lucene search with feature parity. No FTS5 fallback, custom SQLite-backed Lucene Directory, or extra search database is planned. [SQLite FTS5](https://www.sqlite.org/fts5.html).

### Application-facing interfaces

| Interface/type | Responsibility |
| --- | --- |
| `ISearchQueryService` | Browse/filter/sort media, search text/content, suggestions, and related-media results. |
| `ISearchIndexWriter` | Idempotent replace/upsert/delete and explicit durable completion for a batch. |
| `ISearchProjectionSource` | Read authoritative index documents in bounded batches, including retained caption/comment content. |
| `ISearchMaintenance` | Status, schema/generation validation, rebuild progress, and activation. |
| Typed search query | Free text, recognized filters, scope, sorting, and pagination independent of backend syntax. |

Keep existing HTTP endpoints/DTOs when their shape remains useful. A clean internal interface does not require changing every frontend call. Add operational search state only where needed for the UI to explain unavailable/rebuilding search.

Parse the current advanced-search syntax into typed filters. Provider translators generate Typesense filters or Lucene queries; raw Typesense filter strings no longer leave its adapter. Preserve quoted values, aliases, range/date operators, and documented treatment of unknown tokens. Use parameter/typed query construction rather than string concatenation of unchecked user input.

### Parity contract

Both providers must support:

- Library and channel media browsing, filters, sorting, and paging.
- Search over current metadata fields and advanced operators.
- Caption/subtitle and comment matches that identify their parent media and match reason.
- Applicable owner-scoped private-note matches.
- Suggestions, related/similar media, and supported result snippets/highlights.
- Correct scoping, deduplication, missing/deleted records, and stable secondary ordering for pagination.

The common query layer defines response semantics. Provider-specific relevance scores/order may differ; authorization, filters, supported scopes, and paging must not silently change. Characterize existing behavior and distinguish intentional fixes from accidental regressions rather than reproducing every current implementation quirk.

Authorization remains authoritative outside the search index. Trusted request identity is supplied by the API boundary. Private content must not leak through hits, snippets, counts, or suggestions; an index hit is never proof that the caller may read the object. Full account/permission tests must exercise the actual response, including totals.

### Reliable index updates

1. Persist indexing intent in the same database transaction as the application mutation it describes.
2. Track/coalesce pending work by entity and desired revision. Capture enough identity for deletions after the source row is gone.
3. A bounded worker loads current authoritative content and idempotently replaces or removes its indexed representation.
4. Confirm the provider's durable completion before marking that revision applied. Queue acceptance or an in-memory refresh is insufficient.
5. Acknowledge only the revision processed; a newer mutation arriving during indexing must remain pending.
6. Retry failures with bounded backoff and expose persistent failures. Duplicate execution after a crash must be harmless.

Retain deletion intent until all affected media/comment/caption index documents are removed. Do not mark work complete just because an HTTP response or transient event was sent. Full may signal workers through NATS, but the authoritative pending-work record prevents a lost signal from losing an update.

### Index lifecycle and rebuilds

- Use one writer owner per Lite index and bounded batches. Share readers/searchers according to the library lifecycle rather than opening a writer per request.
- Build a replacement generation without clearing the currently valid generation.
- Record mutations during the build and catch them up before activation. Use a short write barrier at the end to establish a consistent activation point; maintenance windows are permitted.
- Activate the validated generation atomically and retain the previous generation until the replacement is confirmed usable.
- A failed/canceled rebuild retains the old usable generation. Incomplete new generations are disposable.
- Missing/corrupt/incompatible indexes trigger a rebuild from authoritative records and sidecars.
- Restore invalidates derived search state; rebuild or validate it before declaring search ready.
- Distinguish capabilities from health: search remains a supported feature while its status is `building`, `ready`, `degraded`, or `failed`.

Non-search application operations should remain available during index rebuild. Search-dependent library pages must explicitly show rebuilding/degraded state, not pretend that an empty or partial index is an empty collection. Existing valid search may continue during replacement builds.

## 6. Local runtime, persistence, and lifecycle

### Persistence extraction

Preserve the shared EF model, repository operations, and explicit provider SQL. Move concrete connection factories, schema setup, provider mappings, migrations, and transaction-specific behavior into the appropriate infrastructure implementation.

Introduce narrow provider extension points for model configuration, operation SQL, connections, and mutation execution where needed. Avoid inventing a universal database abstraction. Shared feature code may continue using EF/repository APIs; it must stop choosing a provider based on deployment flags.

Fresh-install schema baselines can be consolidated after extraction. Keep schema changes separate from host moves and search cutover so failures remain attributable. No old-schema compatibility layer or account ownership conversion is needed.

### SQLite durable work

Retain existing durable-job and workflow semantics initially. Replace the queue's expensive work-discovery behavior behind its interface:

- Represent known routes/consumer deliveries so pending-work selection can use indexed predicates instead of loading all messages and filtering them in managed memory.
- Claim only a bounded batch in a short transaction; run handlers outside that transaction.
- Use local wake-up signals for new work and a bounded fallback poll for delayed retries/expired leases. Back off when idle.
- Keep explicit leases, lease renewal, acknowledgment, deduplication, and retry limits.
- Record terminal failures and expose them for inspection/retry rather than retrying poison messages forever.
- Delete completed delivery records only after all required consumers have finished. Keep deduplication markers for at least the maximum supported retry/redelivery period; retention must never remove active work.
- Keep application job history separate from transport retention so pruning queue rows does not erase user-facing history.

The provider implementation must preserve current startup reconciliation and explicit retry semantics. An interrupted download must not unexpectedly become an automatically resumed download as a side effect of changing transport.

Use SQLite WAL and bounded write transactions on appropriate local storage. WAL improves read/write coexistence but does not provide multiple simultaneous writers; resource control is still required. [SQLite WAL documentation](https://www.sqlite.org/wal.html).

### Explicit initialization

Hosts own a deterministic sequence:

1. Validate edition configuration, paths, permissions, and required dependencies.
2. Initialize application schema and workflow storage before resolving components that use them.
3. Establish local identity where applicable and reconcile interrupted work.
4. Register application request/event handlers and await handler readiness.
5. Open the valid search generation or start a bounded rebuild with an explicit status.
6. Release durable execution and hydrate/start scheduling.
7. Report application readiness; expose search/integration status separately.

Keep constructors and DI registration free of schema writes, network calls, and process-lifetime changes. Prevent workflow watchdogs/consumers from running before reconciliation. Hosted-service startup must not deadlock waiting for a later service to release a gate; the host's coordinator owns stage progression.

Shutdown stops new work, cancels/drains bounded execution, preserves retryable work, commits acknowledged index work, and disposes readers/writers/connections. The host, not Worker or MediaProcessor modules, selects process lifetime.

### Laptop defaults and measurement

Initial defaults are one concurrent transcode, two concurrent downloads, one bounded index writer, bounded caches/batches, and lower-priority maintenance indexing during heavy media work. Make limits configurable without changing edition identity or adding Full infrastructure.

Bind the frontend's published port to loopback by default. Backend/POT/optional ClickHouse remain on the private deployment network. Trusted-LAN opt-in must state that every reachable client has the same Lite Admin privileges; it does not enable an account system.

Measure, rather than promise, laptop performance. Use a fixed reference workload with 10,000 media records, 100,000 caption documents, and 250,000 comments, plus generated media for actual processing. Record corpus byte sizes, hardware, index size, and warm/cold state so results are reproducible. This is a benchmark fixture, not a product library-size limit.

Measure fresh/steady startup, idle CPU and database activity, search latency, full/incremental indexing, playback, and combined download/transcode/index workloads. Include FFmpeg child processes, container memory, and filesystem cache. Track optional ClickHouse separately. Phase 0 establishes measured thresholds and Phase 5 must demonstrate stable operation on an 8 GB machine without unbounded growth or out-of-memory failures.

## 7. Identity, API behavior, and capabilities

### Lite

Keep a stable internal owner identity so playlists, notes, preferences, and other user-scoped features can reuse shared code. This internal identity is not a user-management feature. No registration, external login, sign-out, account switching, or access-management routes are exposed as supported Lite operations.

Local identity and authorization are installed explicitly by the Lite host. Avoid generic configuration that could accidentally enable a Full login flow or provision OpenFGA. Shared endpoints ask the selected authorization implementation instead of embedding edition branches.

### Full

Remove `SINGLE_USER_MODE` and `Auth:SingleUserMode` as supported Full behavior. Reject obsolete flags with a clear configuration error rather than silently downgrading authentication. Missing required Authentik/OpenFGA configuration fails startup.

Authentik is the account authentication authority. Preserve BFF browser login/session behavior and Authentik-issued bearer credentials as needed. Existing scoped cast/podcast delivery tokens remain delegation mechanisms for specific resources, not alternate account systems. Keep their issuer, scope, and permission checks separate from user login.

Full.Security owns account/access-management endpoints and implementations. Registration in the Full API host is explicit; Lite does not load this assembly. Tests may use controlled identities internally, but production Full composition must not expose a single-user/test-auth bypass.

### Frontend and public capabilities

Use one frontend build. Hosts derive capabilities from the actual registered edition/providers, including supported backup operations and optional integrations. Do not infer Lite from a session mode or server URL.

Retain current routes and DTOs where practical to minimize frontend changes. Add provider-neutral search features/provider identity and operational status only where useful to display correct UI. Full-only administration follows capabilities; server-side authorization remains mandatory regardless of hidden controls.

## 8. Implementation phases

Every phase ends with clean-install configurations and a passing regression checkpoint. Phase completion means its behavior and exit gates work, not just that files have moved. Break each phase into reviewable changes; separate mechanical extraction from behavior changes.

Rollback for this fresh-install effort means reverting the phase and recreating owned development/test state. There is no promise of binary or database compatibility with previous phases. Maintenance windows are acceptable; do not build rolling-upgrade infrastructure for this project.

### Phase 0 — Baseline, contracts, and verification

**Work:** Capture current service/dependency graphs, important workflows, API/search behavior, and resource measurements. Establish repeatable clean Lite/Full installations and critical characterization tests. Update the testing strategy for the new target: Full single-user becomes rejection coverage; Lite Lucene replaces Lite Typesense once Phase 5 lands.

**Deliverables:** Architecture/dependency rules, clean environment fixtures, search behavior corpus, measured laptop baseline/acceptance thresholds, and a phase checklist aligned with [TESTS_PLAN.MD](TESTS_PLAN.MD).

**Exit gate:** Download/import/playback and edition authentication run repeatably. Existing differences and defects are named; tests do not encode accidental behavior as an unchangeable requirement.

**Safe stopping point:** No architectural behavior changes yet; the baseline supports the following extractions.

### Phase 1 — Reusable libraries and thin hosts

**Work:** Convert reusable WebAPI/DataBridge/Worker/MediaProcessor/Scheduler functionality to class libraries. Create/move Full executable shells and preserve the combined Lite executable. Move appsettings, process lifetime, and endpoint composition to hosts. Update project references, Docker builds, AppHosts, and tests together.

**Exit gate:** No executable-to-executable references; no appsettings output-removal workaround; both fresh deployments retain characterized behavior.

**Safe stopping point:** Clear process boundaries even while some provider registration remains temporarily in modules.

### Phase 2 — Explicit edition infrastructure and identity

**Status:** Implemented in `new_refactor_src/`. Edition-owned adapters now select persistence, messaging, secrets, backups and identity explicitly; shared modules and persistence compile without their provider SDKs. Full security belongs to the Full WebAPI host, and obsolete Full single-user configuration fails startup. Named initialization stages and a handler-registration barrier replace constructor I/O and hosted-service reordering. See [Phase 2 implementation and verification](new_refactor_src/Phase2/README.md) and [results](new_refactor_src/Phase2/verification-results.json). Typesense remains the documented temporary Lite exception; Phase 0's outstanding deployment and measurement gates remain open.

**Work:** Extract provider-specific implementation/registration, slim shared dependencies, move Full security out of shared API, replace registration-order manipulation with lifecycle coordination, and remove Full single-user behavior.

**Exit gate:** Lite cannot register NATS/PostgreSQL/OpenBao/Full security and no longer depends on their SDKs. The isolated Typesense adapter remains the documented temporary exception. Full requires Authentik and rejects single-user configuration. Initialization/shutdown/restart tests pass.

**Safe stopping point:** Both editions remain functional with explicit composition. Lite still uses Typesense until the embedded provider is ready.

### Phase 3 — Reliable and bounded local runtime

**Status:** Implemented in `new_refactor_src/`. Local delivery claims and retention use indexed, bounded pages, subject-specific wake-ups and idle backoff, explicit concurrency limits, fenced leases, retained terminal failures and deduplication markers. Both providers persist revisioned index intent in source transactions; a bounded Typesense worker applies it and retains failures/deletions for retry. Media child processes have shared limits and cancellation cleanup. See [Phase 3 implementation](new_refactor_src/Phase3/README.md) and [verification evidence](new_refactor_src/Phase3/verification-results.json). Full deployment and reference-machine measurements still belong to the open Phase 0 gates.

**Work:** Add indexed/bounded queue claims, wake-up/backoff behavior, retention, terminal retry handling, and concurrency controls. Introduce transactional pending-index work and neutral route definitions. Adapt current indexing to consume durable intent without replacing business workflows.

**Exit gate:** Idle polling cost stays bounded; crash/redelivery/deduplication/retention tests pass; index intent survives database commit and process failure; no handler executes inside a long database transaction.

**Safe stopping point:** Improved laptop runtime and reliable indexing while the existing Typesense provider is still active.

### Phase 4 — Complete search boundary, retaining Typesense

**Status:** Implemented in `new_refactor_src/`. Shared handlers, typed parsing, result mapping, projection sourcing, durable indexing, and rebuild orchestration now use provider-neutral ports. Both editions retain the isolated Typesense adapter. Replacement generations activate through one persistent alias; failed/canceled builds retain active search. The solution builds and all 609 unit tests pass with real Typesense contracts enabled. See [Phase 4 implementation and limits](new_refactor_src/Phase4/README.md) and [verification evidence](new_refactor_src/Phase4/verification-results.json). Packaged Full deployment and Phase 0 measurement gates remain open.

**Work:** Extract typed query parsing, common result semantics, projection sourcing, maintenance/status, and a Typesense adapter. Move every direct Typesense consumer, including library browsing, comments, captions, and related media, behind the boundary.

**Exit gate:** Both deployments pass search contract tests using Typesense through the new interfaces. Shared modules no longer consume Typesense query/result/schema types. Rebuild failure does not clear a valid active index.

**Safe stopping point:** A provider-independent search subsystem; Full is already on its intended engine.

### Phase 5 — Embedded Lite search and service removal

**Work:** Implement Lucene query translation/index writing, feature parity, durable commit/replay, generation rebuilds, and operational status. Switch Lite composition to Lucene. Remove Typesense from Lite AppHost, generated Compose, environment settings, publish dependencies, fixtures, and documentation.

**Exit gate:** Lite runs with backend/frontend/POT only; optional ClickHouse adds one container. All shared search features pass. Missing/corrupt index rebuild and concurrent update tests pass. The laptop benchmark meets the Phase 0 acceptance thresholds.

**Safe stopping point:** The intended lightweight product deployment exists, without Typesense or the other Full services.

### Phase 6 — Fresh-install packaging and recovery

**Work:** Consolidate fresh-install schema baselines/configuration, finalize volumes and resource defaults, document ownership of media/chat backups, validate optional integrations, and verify both editions' recovery procedures and readiness reporting.

**Exit gate:** Clean install, restart, Lite snapshot/key-ring recovery, Full PostgreSQL/OpenBao recovery, and live-chat on/off profiles pass from the packaged artifacts.

**Safe stopping point:** Deployment and recovery match the target architecture, with measured operating guidance.

### Phase 7 — Remove transitional paths and enforce boundaries

**Work:** Remove obsolete selectors/markers, old configuration aliases, unused adapters, duplicate helpers, stale Full single-user code, and transitional Typesense references. Update architecture/test documentation and enforce dependency rules in CI.

**Exit gate:** No transitional paths remain; both clean-install profiles and required backend/UI suites pass; new boundary violations fail CI.

**Safe stopping point:** Refactor complete. Future features are implemented once and exercised in both compositions.

## 9. Verification strategy and acceptance gates

Use the existing backend tests and the broader UI strategy in [TESTS_PLAN.MD](TESTS_PLAN.MD). Add checks alongside each phase; do not wait for the final phase to discover composition failures.

| Area | Required scenarios |
| --- | --- |
| Architecture | No cycles or executable references; host-only composition; no vendor types leaking through search/contracts; final Lite package graph excludes Full SDKs. |
| Persistence | Clean SQLite/PostgreSQL initialization; repository/SQL parity; correct transaction boundaries; concurrent writes; fresh baseline validation. |
| Lite lifecycle | Deterministic startup/readiness, shutdown, idle behavior, queue contention, leases, duplicate delivery, retries, pruning, crash reconciliation, explicit new run after interruption. |
| Full identity | Real Authentik login, OpenFGA allow/deny, private-data isolation, required configuration checks, rejection of all former single-user configuration paths. |
| Search parity | All scopes/advanced filters/sorts; Unicode/quotes/ranges; paging and deduplication; related media; private-note visibility; missing/deleted documents and correct totals. |
| Index durability | Database commit followed by lost signal; failure before/after index commit; duplicate work; new mutation during indexing; deleted source; backlog recovery after restart. |
| Rebuild | Concurrent mutation, activation boundary, cancellation, missing/corrupt index, incompatible schema, disk full, and failed rebuild preserving the old valid generation. |
| Product workflows | Real download/import/process/index/search/playback; playlists/notes/preferences; schedules; capability-dependent frontend behavior. |
| Recovery | Lite SQLite plus complete key-ring restore; Full PostgreSQL/OpenBao restore; invalidation/rebuild of derived search; media/chat recovery scope stated accurately. |
| Deployment | Candidate package clean installs on supported architectures; Lite three-container default; no attempted connections to removed infrastructure; optional ClickHouse truly optional. |
| Laptop performance | Measured memory/CPU/latency and sustained bounded growth in idle/indexing/playback/combined workloads on the specified reference machine. |

Do not treat relevance-score equality as search parity. Test the required results, filters, isolation, and common response contract; document ranking differences. Do not treat a mocked successful operation as end-to-end infrastructure coverage.

### Changes required to the earlier testing plan

The previous test plan describes the current application's capabilities as well as future test work. This architecture proposal supersedes its following assumptions:

- Replace the Full single-user compatibility project/cases with startup rejection and Authentik-only Full coverage.
- Replace Lite Typesense fixture/dependency expectations with Lucene after Phase 5.
- Add common provider search contracts, durable index intent, index-generation recovery, and rebuild status UI cases.
- Add transitive dependency/host-boundary checks and Lite resource tests.
- Treat fresh installation and owned disposable state as the refactor's rollout model; do not add old-data migration testing.

The current documentation deliverable does not modify `TESTS_PLAN.MD`; Phase 0 reconciles that document and its implementation work explicitly.

## 10. Completion criteria and implementation discipline

The architecture refactor is complete when:

- Lite has one .NET backend and the specified supporting containers, with embedded SQLite and Lucene.
- Full retains its service separation/infrastructure and supports Authentik accounts only.
- Shared modules contain one implementation of each application feature and select no edition infrastructure.
- Lite's dependency graph and runtime connections contain no Full-only provider implementation.
- All search-backed UI areas work on both providers; the Lucene index is independently rebuildable.
- Index intent is durable, deletion/retry behavior is correct, and rebuilds do not discard a valid working generation.
- Local queue, media execution, and indexing have bounded resource behavior validated on the laptop target.
- Startup/shutdown/recovery ordering is explicit and independently tested.
- Fresh install and documented recovery work from packaged artifacts for both editions.
- Transitional configuration, registration hacks, and stale documentation are removed.

Keep changes reviewable: one boundary or behavior change at a time, followed by its exit gate. Do not combine a host extraction, database redesign, workflow replacement, and search cutover into one change. Fresh-install freedom removes compatibility obligations; it does not require redesigning working business logic.

Creating this document completes the architecture-review/proposal deliverable. It does not execute the phases, provision environments, reset data, install libraries, or implement the refactor.
