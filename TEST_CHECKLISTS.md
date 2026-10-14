# FrostStream test checklists

This is the acceptance checklist for backend unit/integration testing and frontend end-to-end (E2E) testing. Implementation instructions, fixtures, requirements, commands and evidence rules are in [TEST_IMPLEMENTATION_GUIDE.md](TEST_IMPLEMENTATION_GUIDE.md). Paths are relative to the repository root and reflect the current `src/` layout.

**This is a testing plan, not a report of passing tests.** Every item starts unchecked. Existing tests are starting points to audit and extend; historical reports do not close a new checklist item. These documents do not add tests, dependencies or GitHub Actions. GitHub Actions work is deferred until the user finishes the testing effort and explicitly requests it; completing this checklist does not authorize CI changes.

## How to use the checklist

Each ID links to a matching implementation specification. Core means the shared contracts, domain, persistence, API, DataBridge, Worker, MediaProcessor and Scheduler behavior used by both editions; it is broader than the `Shared/Core` directory. Full and Lite contain edition-specific behavior. Frontend Core cases run against **both real backends**, not a third mock Core backend.

| Label | Meaning |
| --- | --- |
| Unit | Isolated logic with fakes; no real database, network, browser or external process. |
| Integration | Real boundaries such as SQLite/PostgreSQL, service middleware, messaging, search, storage, subprocesses or composed components. A test named `UnitTests` can belong here. |
| E2E | Browser-driven product journey through the actual frontend and edition backend. Controlled external fixtures are allowed and must be disclosed; mocking FrostStream API success is not E2E evidence. |
| P0 | Implement first: data integrity, identity/authorization, durable work, recovery, edition selection and primary media journeys. |
| P1 | Required feature, negative-path and operational coverage. |
| P2 | Required extended acceptance with hardware, larger corpus or sustained-load prerequisites. Priority does not make a case optional. |

An item can require several separately reported cases, such as valid/invalid input, both database providers or all advertised storage protocols. Expand these as named parameterized tests and record every required variant. Check the parent only when all variants pass. `Blocked`, `Not implemented`, `Implemented/unverified`, `Failing` and `Passed` belong in the local run manifest; an unchecked box distinguishes all unfinished states. Use N/A only for a documented unsupported edition/browser/protocol combination in the matrix, with the reason; unavailable hardware or a broken feature is blocked, not N/A.

For each checked box, record its implementation file/test names, commit, command/profile, provider/edition/browser, prerequisite versions, result and evidence location. See the [completion rules](TEST_IMPLEMENTATION_GUIDE.md#completion-and-evidence). A green runner exit with zero selected cases, omitted providers, early-returned bodies or retries hiding the first failure cannot close an item.

## Required run matrix

| Area | Required execution |
| --- | --- |
| Backend Core units | Once per implementation with all input variants. |
| Core persistence contracts | SQLite and PostgreSQL independently, using real relational databases. |
| Core search contracts | Lucene and Typesense independently. |
| Core composed workflows | Both production edition compositions; controlled acquisition plus a separate real yt-dlp/local-origin acquisition case. |
| Backend Full | Real NATS/PostgreSQL/OpenFGA/OpenBao as relevant; real Authentik for OIDC, plus Full backup and distributed-host profiles. |
| Backend Lite | Real SQLite/local durable transport/encrypted secrets/Lucene and single-host runtime; no required Full services. |
| Frontend Core | Full and Lite, desktop Chromium for the entire suite; cross-browser and viewport requirements below. |
| Frontend Full / Lite | Matching edition only; Full includes independent user roles and the separate BackupService restore console. |
| Browser compatibility | Run shared navigation, forms, authentication where applicable, search, original/HLS/audio playback, captions, resume, dialogs and import on desktop Chromium/Firefox/WebKit and the specified phone/tablet viewports in both editions. Record codec/device exceptions explicitly. |
| Optional features | Baseline off for each edition; live chat and POT enabled separately, unavailable, and recovered; combined enabled smoke. |
| Storage providers / casting | Every advertised backend protocol has real transfer evidence; browser cast features require an appropriate real device/browser or faithful server receiver as specified. |
| Recovery / performance | Separate disposable environments and documented reference workload/hardware; recovery success includes post-restore application behavior. |

Implementation order: fixture/discovery foundations → Core units/contracts → Lite runtime → Full security/distributed runtime → frontend shared journeys → edition/optional/device journeys → recovery and extended acceptance. Security, migration and media-transfer regressions are early gates. [Implementation sequence](TEST_IMPLEMENTATION_GUIDE.md#implementation-sequence) explains dependencies.

## Checklist totals

| Suite | Unit | Integration | E2E |
| --- | ---: | ---: | ---: |
| [Backend Core — unit tests](#suite-core-u) | 40 | 0 | 0 |
| [Backend Core — integration contracts](#suite-core-i) | 0 | 39 | 0 |
| [Backend Full — unit tests](#suite-full-u) | 13 | 0 | 0 |
| [Backend Full — integration tests](#suite-full-i) | 0 | 22 | 0 |
| [Backend Lite — unit tests](#suite-lite-u) | 8 | 0 | 0 |
| [Backend Lite — integration tests](#suite-lite-i) | 0 | 20 | 0 |
| [Frontend supporting unit tests](#suite-ui-u) | 6 | 0 | 0 |
| [Frontend supporting component integration tests](#suite-ui-i) | 0 | 2 | 0 |
| [Frontend Core — E2E against both editions](#suite-ui-core-e) | 0 | 0 | 45 |
| [Frontend Full — E2E](#suite-ui-full-e) | 0 | 0 | 14 |
| [Frontend Lite — E2E](#suite-ui-lite-e) | 0 | 0 | 8 |
| [Frontend optional integrations and device E2E](#suite-ui-opt-e) | 0 | 0 | 8 |
| [System acceptance exercised as integration tests](#suite-accept-i) | 0 | 7 | 0 |
| **Total (232 checklist items; parameter variants add executions)** | **67** | **90** | **75** |

## Test inventory

All scenarios below require the shared fixture/validation rules in the guide as well as their individual specification.

<a id="suite-core-u"></a>

### Backend Core — unit tests

<a id="core-u-001"></a>

- [ ] **[CORE-U-001](TEST_IMPLEMENTATION_GUIDE.md#core-u-001)** — Unit · Both · P0 — Deployment mode parsing and obsolete switches.

<a id="core-u-002"></a>

- [ ] **[CORE-U-002](TEST_IMPLEMENTATION_GUIDE.md#core-u-002)** — Unit · Both · P0 — Capability contract for both editions.

<a id="core-u-003"></a>

- [ ] **[CORE-U-003](TEST_IMPLEMENTATION_GUIDE.md#core-u-003)** — Unit · Both · P0 — Module registration is idempotent.

<a id="core-u-004"></a>

- [ ] **[CORE-U-004](TEST_IMPLEMENTATION_GUIDE.md#core-u-004)** — Unit · Both · P0 — Startup gate completion, failure and cancellation.

<a id="core-u-005"></a>

- [ ] **[CORE-U-005](TEST_IMPLEMENTATION_GUIDE.md#core-u-005)** — Unit · Both · P0 — Message serialization contracts.

<a id="core-u-006"></a>

- [ ] **[CORE-U-006](TEST_IMPLEMENTATION_GUIDE.md#core-u-006)** — Unit · Both · P1 — Background job routing and request identity.

<a id="core-u-007"></a>

- [ ] **[CORE-U-007](TEST_IMPLEMENTATION_GUIDE.md#core-u-007)** — Unit · Both · P0 — Source URL validation and canonical identity.

<a id="core-u-008"></a>

- [ ] **[CORE-U-008](TEST_IMPLEMENTATION_GUIDE.md#core-u-008)** — Unit · Both · P1 — Ignore keyword matching.

<a id="core-u-009"></a>

- [ ] **[CORE-U-009](TEST_IMPLEMENTATION_GUIDE.md#core-u-009)** — Unit · Both · P0 — Stable media and creator identifiers.

<a id="core-u-010"></a>

- [ ] **[CORE-U-010](TEST_IMPLEMENTATION_GUIDE.md#core-u-010)** — Unit · Both · P1 — Metadata mapping and partial provider payloads.

<a id="core-u-011"></a>

- [ ] **[CORE-U-011](TEST_IMPLEMENTATION_GUIDE.md#core-u-011)** — Unit · Both · P1 — Placeholder content detection.

<a id="core-u-012"></a>

- [ ] **[CORE-U-012](TEST_IMPLEMENTATION_GUIDE.md#core-u-012)** — Unit · Both · P1 — Download option merging and config resolution.

<a id="core-u-013"></a>

- [ ] **[CORE-U-013](TEST_IMPLEMENTATION_GUIDE.md#core-u-013)** — Unit · Both · P1 — Worker option limits.

<a id="core-u-014"></a>

- [ ] **[CORE-U-014](TEST_IMPLEMENTATION_GUIDE.md#core-u-014)** — Unit · Both · P1 — Acquisition failure classification.

<a id="core-u-015"></a>

- [ ] **[CORE-U-015](TEST_IMPLEMENTATION_GUIDE.md#core-u-015)** — Unit · Both · P0 — Local import path validation.

<a id="core-u-016"></a>

- [ ] **[CORE-U-016](TEST_IMPLEMENTATION_GUIDE.md#core-u-016)** — Unit · Both · P1 — Storage parameter validation and JSON.

<a id="core-u-017"></a>

- [ ] **[CORE-U-017](TEST_IMPLEMENTATION_GUIDE.md#core-u-017)** — Unit · Both · P0 — Storage secret separation.

<a id="core-u-018"></a>

- [ ] **[CORE-U-018](TEST_IMPLEMENTATION_GUIDE.md#core-u-018)** — Unit · Both · P0 — Rooted storage path resolution.

<a id="core-u-019"></a>

- [ ] **[CORE-U-019](TEST_IMPLEMENTATION_GUIDE.md#core-u-019)** — Unit · Both · P1 — Storage cache invalidation.

<a id="core-u-020"></a>

- [ ] **[CORE-U-020](TEST_IMPLEMENTATION_GUIDE.md#core-u-020)** — Unit · Both · P1 — Storage provider selection.

<a id="core-u-021"></a>

- [ ] **[CORE-U-021](TEST_IMPLEMENTATION_GUIDE.md#core-u-021)** — Unit · Both · P1 — Advanced search query parsing.

<a id="core-u-022"></a>

- [ ] **[CORE-U-022](TEST_IMPLEMENTATION_GUIDE.md#core-u-022)** — Unit · Both · P0 — Search generation and writer gate rules.

<a id="core-u-023"></a>

- [ ] **[CORE-U-023](TEST_IMPLEMENTATION_GUIDE.md#core-u-023)** — Unit · Both · P1 — Endpoint permission catalog completeness.

<a id="core-u-024"></a>

- [ ] **[CORE-U-024](TEST_IMPLEMENTATION_GUIDE.md#core-u-024)** — Unit · Both · P0 — Safe return URLs.

<a id="core-u-025"></a>

- [ ] **[CORE-U-025](TEST_IMPLEMENTATION_GUIDE.md#core-u-025)** — Unit · Both · P0 — Shared controller validation and status contracts.

<a id="core-u-026"></a>

- [ ] **[CORE-U-026](TEST_IMPLEMENTATION_GUIDE.md#core-u-026)** — Unit · Both · P0 — Request ownership propagation.

<a id="core-u-027"></a>

- [ ] **[CORE-U-027](TEST_IMPLEMENTATION_GUIDE.md#core-u-027)** — Unit · Both · P1 — Download queue and job-state decisions.

<a id="core-u-028"></a>

- [ ] **[CORE-U-028](TEST_IMPLEMENTATION_GUIDE.md#core-u-028)** — Unit · Both · P1 — Progress aggregation and subscriber lifecycle.

<a id="core-u-029"></a>

- [ ] **[CORE-U-029](TEST_IMPLEMENTATION_GUIDE.md#core-u-029)** — Unit · Both · P1 — Schedule parsing and job dispatch.

<a id="core-u-030"></a>

- [ ] **[CORE-U-030](TEST_IMPLEMENTATION_GUIDE.md#core-u-030)** — Unit · Both · P1 — Notification profile validation.

<a id="core-u-031"></a>

- [ ] **[CORE-U-031](TEST_IMPLEMENTATION_GUIDE.md#core-u-031)** — Unit · Both · P1 — Media access and rendition selection.

<a id="core-u-032"></a>

- [ ] **[CORE-U-032](TEST_IMPLEMENTATION_GUIDE.md#core-u-032)** — Unit · Both · P0 — Podcast and cast token scope.

<a id="core-u-033"></a>

- [ ] **[CORE-U-033](TEST_IMPLEMENTATION_GUIDE.md#core-u-033)** — Unit · Both · P1 — Cast URL and session state.

<a id="core-u-034"></a>

- [ ] **[CORE-U-034](TEST_IMPLEMENTATION_GUIDE.md#core-u-034)** — Unit · Both · P1 — FFmpeg argument and process limits.

<a id="core-u-035"></a>

- [ ] **[CORE-U-035](TEST_IMPLEMENTATION_GUIDE.md#core-u-035)** — Unit · Both · P1 — Chat replay parsing.

<a id="core-u-036"></a>

- [ ] **[CORE-U-036](TEST_IMPLEMENTATION_GUIDE.md#core-u-036)** — Unit · Both · P1 — Backup job request validation.

<a id="core-u-037"></a>

- [ ] **[CORE-U-037](TEST_IMPLEMENTATION_GUIDE.md#core-u-037)** — Unit · Both · P1 — Media deletion selection and reporting.

<a id="core-u-038"></a>

- [ ] **[CORE-U-038](TEST_IMPLEMENTATION_GUIDE.md#core-u-038)** — Unit · Both · P1 — Optional enrichment behavior.

<a id="core-u-039"></a>

- [ ] **[CORE-U-039](TEST_IMPLEMENTATION_GUIDE.md#core-u-039)** — Unit · Both · P1 — Worker discovery and heartbeat expiry.

<a id="core-u-040"></a>

- [ ] **[CORE-U-040](TEST_IMPLEMENTATION_GUIDE.md#core-u-040)** — Unit · Both · P1 — Query parameter bounds.


<a id="suite-core-i"></a>

### Backend Core — integration contracts

<a id="core-i-001"></a>

- [ ] **[CORE-I-001](TEST_IMPLEMENTATION_GUIDE.md#core-i-001)** — Integration · Both providers / editions · P0 — Fresh schema and repeatable startup.

<a id="core-i-002"></a>

- [ ] **[CORE-I-002](TEST_IMPLEMENTATION_GUIDE.md#core-i-002)** — Integration · Both providers / editions · P0 — Supported schema upgrades.

<a id="core-i-003"></a>

- [ ] **[CORE-I-003](TEST_IMPLEMENTATION_GUIDE.md#core-i-003)** — Integration · Both providers / editions · P0 — Failed, canceled and concurrent schema upgrades.

<a id="core-i-004"></a>

- [ ] **[CORE-I-004](TEST_IMPLEMENTATION_GUIDE.md#core-i-004)** — Integration · Both providers / editions · P0 — Metadata and account repository contracts.

<a id="core-i-005"></a>

- [ ] **[CORE-I-005](TEST_IMPLEMENTATION_GUIDE.md#core-i-005)** — Integration · Both providers / editions · P0 — Concurrent writes and transactional rollback.

<a id="core-i-006"></a>

- [ ] **[CORE-I-006](TEST_IMPLEMENTATION_GUIDE.md#core-i-006)** — Integration · Both providers / editions · P0 — Durable workflow state and fencing.

<a id="core-i-007"></a>

- [ ] **[CORE-I-007](TEST_IMPLEMENTATION_GUIDE.md#core-i-007)** — Integration · Both providers / editions · P0 — Download submission through finalization.

<a id="core-i-008"></a>

- [ ] **[CORE-I-008](TEST_IMPLEMENTATION_GUIDE.md#core-i-008)** — Integration · Both providers / editions · P0 — Stop, restart and explicit retry.

<a id="core-i-009"></a>

- [ ] **[CORE-I-009](TEST_IMPLEMENTATION_GUIDE.md#core-i-009)** — Integration · Both providers / editions · P1 — Queue priorities, groups and cleanup.

<a id="core-i-010"></a>

- [ ] **[CORE-I-010](TEST_IMPLEMENTATION_GUIDE.md#core-i-010)** — Integration · Both providers / editions · P1 — Creator discovery and playlists.

<a id="core-i-011"></a>

- [ ] **[CORE-I-011](TEST_IMPLEMENTATION_GUIDE.md#core-i-011)** — Integration · Both providers / editions · P0 — Import session lifecycle.

<a id="core-i-012"></a>

- [ ] **[CORE-I-012](TEST_IMPLEMENTATION_GUIDE.md#core-i-012)** — Integration · Both providers / editions · P0 — Import rejection, retry, cancel and cleanup.

<a id="core-i-013"></a>

- [ ] **[CORE-I-013](TEST_IMPLEMENTATION_GUIDE.md#core-i-013)** — Integration · Both providers / editions · P0 — Import filesystem containment.

<a id="core-i-014"></a>

- [ ] **[CORE-I-014](TEST_IMPLEMENTATION_GUIDE.md#core-i-014)** — Integration · Both providers / editions · P0 — Storage CRUD and credentials.

<a id="core-i-015"></a>

- [ ] **[CORE-I-015](TEST_IMPLEMENTATION_GUIDE.md#core-i-015)** — Integration · Both providers / editions · P1 — Storage invalidation and failure recovery.

<a id="core-i-016"></a>

- [ ] **[CORE-I-016](TEST_IMPLEMENTATION_GUIDE.md#core-i-016)** — Integration · Both providers / editions · P0 — Original media HTTP semantics.

<a id="core-i-017"></a>

- [ ] **[CORE-I-017](TEST_IMPLEMENTATION_GUIDE.md#core-i-017)** — Integration · Both providers / editions · P0 — Video rendition pipeline.

<a id="core-i-018"></a>

- [ ] **[CORE-I-018](TEST_IMPLEMENTATION_GUIDE.md#core-i-018)** — Integration · Both providers / editions · P1 — Audio rendition and podcast delivery.

<a id="core-i-019"></a>

- [ ] **[CORE-I-019](TEST_IMPLEMENTATION_GUIDE.md#core-i-019)** — Integration · Both providers / editions · P1 — Thumbnail and asset generation.

<a id="core-i-020"></a>

- [ ] **[CORE-I-020](TEST_IMPLEMENTATION_GUIDE.md#core-i-020)** — Integration · Both providers / editions · P1 — Captions, comments and sidecars.

<a id="core-i-021"></a>

- [ ] **[CORE-I-021](TEST_IMPLEMENTATION_GUIDE.md#core-i-021)** — Integration · Both providers / editions · P0 — Search semantic parity.

<a id="core-i-022"></a>

- [ ] **[CORE-I-022](TEST_IMPLEMENTATION_GUIDE.md#core-i-022)** — Integration · Both providers / editions · P0 — Incremental indexing and tombstones.

<a id="core-i-023"></a>

- [ ] **[CORE-I-023](TEST_IMPLEMENTATION_GUIDE.md#core-i-023)** — Integration · Both providers / editions · P0 — Search rebuild activation and rollback.

<a id="core-i-024"></a>

- [ ] **[CORE-I-024](TEST_IMPLEMENTATION_GUIDE.md#core-i-024)** — Integration · Both providers / editions · P1 — Watch state and likes persistence.

<a id="core-i-025"></a>

- [ ] **[CORE-I-025](TEST_IMPLEMENTATION_GUIDE.md#core-i-025)** — Integration · Both providers / editions · P0 — User playlists and notes ownership.

<a id="core-i-026"></a>

- [ ] **[CORE-I-026](TEST_IMPLEMENTATION_GUIDE.md#core-i-026)** — Integration · Both providers / editions · P1 — Cookie profiles, option presets and config sets.

<a id="core-i-027"></a>

- [ ] **[CORE-I-027](TEST_IMPLEMENTATION_GUIDE.md#core-i-027)** — Integration · Both providers / editions · P1 — Notification preferences and dispatch.

<a id="core-i-028"></a>

- [ ] **[CORE-I-028](TEST_IMPLEMENTATION_GUIDE.md#core-i-028)** — Integration · Both providers / editions · P1 — Persistent schedules and maintenance.

<a id="core-i-029"></a>

- [ ] **[CORE-I-029](TEST_IMPLEMENTATION_GUIDE.md#core-i-029)** — Integration · Both providers / editions · P1 — Job progress streams and disconnects.

<a id="core-i-030"></a>

- [ ] **[CORE-I-030](TEST_IMPLEMENTATION_GUIDE.md#core-i-030)** — Integration · Both providers / editions · P0 — Media deletion across database, storage and search.

<a id="core-i-031"></a>

- [ ] **[CORE-I-031](TEST_IMPLEMENTATION_GUIDE.md#core-i-031)** — Integration · Both providers / editions · P1 — Bounded retention.

<a id="core-i-032"></a>

- [ ] **[CORE-I-032](TEST_IMPLEMENTATION_GUIDE.md#core-i-032)** — Integration · Both providers / editions · P1 — Optional integrations disabled.

<a id="core-i-033"></a>

- [ ] **[CORE-I-033](TEST_IMPLEMENTATION_GUIDE.md#core-i-033)** — Integration · Both providers / editions · P1 — Live chat ingestion and queries.

<a id="core-i-034"></a>

- [ ] **[CORE-I-034](TEST_IMPLEMENTATION_GUIDE.md#core-i-034)** — Integration · Both providers / editions · P1 — Optional integration outage and recovery.

<a id="core-i-035"></a>

- [ ] **[CORE-I-035](TEST_IMPLEMENTATION_GUIDE.md#core-i-035)** — Integration · Both providers / editions · P1 — Process execution cleanup.

<a id="core-i-036"></a>

- [ ] **[CORE-I-036](TEST_IMPLEMENTATION_GUIDE.md#core-i-036)** — Integration · Both providers / editions · P0 — Endpoint contract parity.

<a id="core-i-037"></a>

- [ ] **[CORE-I-037](TEST_IMPLEMENTATION_GUIDE.md#core-i-037)** — Integration · Both providers / editions · P1 — Statistics and background history accuracy.

<a id="core-i-038"></a>

- [ ] **[CORE-I-038](TEST_IMPLEMENTATION_GUIDE.md#core-i-038)** — Integration · Both providers / editions · P1 — Storage protocol transfer matrix.

<a id="core-i-039"></a>

- [ ] **[CORE-I-039](TEST_IMPLEMENTATION_GUIDE.md#core-i-039)** — Integration · Both providers / editions · P1 — Host readiness and orderly shutdown.


<a id="suite-full-u"></a>

### Backend Full — unit tests

<a id="full-u-001"></a>

- [ ] **[FULL-U-001](TEST_IMPLEMENTATION_GUIDE.md#full-u-001)** — Unit · Full · P0 — Full authentication configuration.

<a id="full-u-002"></a>

- [ ] **[FULL-U-002](TEST_IMPLEMENTATION_GUIDE.md#full-u-002)** — Unit · Full · P0 — Access token validation.

<a id="full-u-003"></a>

- [ ] **[FULL-U-003](TEST_IMPLEMENTATION_GUIDE.md#full-u-003)** — Unit · Full · P0 — Permission evaluation and fail-closed behavior.

<a id="full-u-004"></a>

- [ ] **[FULL-U-004](TEST_IMPLEMENTATION_GUIDE.md#full-u-004)** — Unit · Full · P0 — Access policy precedence and reconciliation decisions.

<a id="full-u-005"></a>

- [ ] **[FULL-U-005](TEST_IMPLEMENTATION_GUIDE.md#full-u-005)** — Unit · Full · P1 — OpenFGA model and tuple generation.

<a id="full-u-006"></a>

- [ ] **[FULL-U-006](TEST_IMPLEMENTATION_GUIDE.md#full-u-006)** — Unit · Full · P1 — Bundle administration validation.

<a id="full-u-007"></a>

- [ ] **[FULL-U-007](TEST_IMPLEMENTATION_GUIDE.md#full-u-007)** — Unit · Full · P0 — CSRF policy.

<a id="full-u-008"></a>

- [ ] **[FULL-U-008](TEST_IMPLEMENTATION_GUIDE.md#full-u-008)** — Unit · Full · P0 — Session refresh and revocation decisions.

<a id="full-u-009"></a>

- [ ] **[FULL-U-009](TEST_IMPLEMENTATION_GUIDE.md#full-u-009)** — Unit · Full · P0 — MediaProcessor service credential propagation.

<a id="full-u-010"></a>

- [ ] **[FULL-U-010](TEST_IMPLEMENTATION_GUIDE.md#full-u-010)** — Unit · Full · P1 — NATS topology configuration.

<a id="full-u-011"></a>

- [ ] **[FULL-U-011](TEST_IMPLEMENTATION_GUIDE.md#full-u-011)** — Unit · Full · P1 — OpenBao path and error mapping.

<a id="full-u-012"></a>

- [ ] **[FULL-U-012](TEST_IMPLEMENTATION_GUIDE.md#full-u-012)** — Unit · Full · P1 — Backup coordination and pgBackRest parsing.

<a id="full-u-013"></a>

- [ ] **[FULL-U-013](TEST_IMPLEMENTATION_GUIDE.md#full-u-013)** — Unit · Full · P0 — Backup and OpenBao artifact pairing.


<a id="suite-full-i"></a>

### Backend Full — integration tests

<a id="full-i-001"></a>

- [ ] **[FULL-I-001](TEST_IMPLEMENTATION_GUIDE.md#full-i-001)** — Integration · Full · P0 — Production Full host composition.

<a id="full-i-002"></a>

- [ ] **[FULL-I-002](TEST_IMPLEMENTATION_GUIDE.md#full-i-002)** — Integration · Full · P0 — Real OIDC login and token exchange.

<a id="full-i-003"></a>

- [ ] **[FULL-I-003](TEST_IMPLEMENTATION_GUIDE.md#full-i-003)** — Integration · Full · P0 — Session refresh, logout and revocation across instances.

<a id="full-i-004"></a>

- [ ] **[FULL-I-004](TEST_IMPLEMENTATION_GUIDE.md#full-i-004)** — Integration · Full · P0 — OpenFGA endpoint and resource authorization.

<a id="full-i-005"></a>

- [ ] **[FULL-I-005](TEST_IMPLEMENTATION_GUIDE.md#full-i-005)** — Integration · Full · P0 — Cross-user personal data isolation.

<a id="full-i-006"></a>

- [ ] **[FULL-I-006](TEST_IMPLEMENTATION_GUIDE.md#full-i-006)** — Integration · Full · P0 — CSRF through the real HTTP pipeline.

<a id="full-i-007"></a>

- [ ] **[FULL-I-007](TEST_IMPLEMENTATION_GUIDE.md#full-i-007)** — Integration · Full · P0 — Internal media transfer authorization.

<a id="full-i-008"></a>

- [ ] **[FULL-I-008](TEST_IMPLEMENTATION_GUIDE.md#full-i-008)** — Integration · Full · P0 — Distributed download, import and HLS pipeline.

<a id="full-i-009"></a>

- [ ] **[FULL-I-009](TEST_IMPLEMENTATION_GUIDE.md#full-i-009)** — Integration · Full · P0 — NATS durable redelivery and restart.

<a id="full-i-010"></a>

- [ ] **[FULL-I-010](TEST_IMPLEMENTATION_GUIDE.md#full-i-010)** — Integration · Full · P1 — NATS request/reply failures and bounds.

<a id="full-i-011"></a>

- [ ] **[FULL-I-011](TEST_IMPLEMENTATION_GUIDE.md#full-i-011)** — Integration · Full · P0 — PostgreSQL outage and recovery.

<a id="full-i-012"></a>

- [ ] **[FULL-I-012](TEST_IMPLEMENTATION_GUIDE.md#full-i-012)** — Integration · Full · P0 — OpenBao persistence, restart and rotation.

<a id="full-i-013"></a>

- [ ] **[FULL-I-013](TEST_IMPLEMENTATION_GUIDE.md#full-i-013)** — Integration · Full · P0 — Required security service failure.

<a id="full-i-014"></a>

- [ ] **[FULL-I-014](TEST_IMPLEMENTATION_GUIDE.md#full-i-014)** — Integration · Full · P1 — Access bundle/policy reconciliation.

<a id="full-i-015"></a>

- [ ] **[FULL-I-015](TEST_IMPLEMENTATION_GUIDE.md#full-i-015)** — Integration · Full · P0 — Typesense provisioning and rebuild recovery.

<a id="full-i-016"></a>

- [ ] **[FULL-I-016](TEST_IMPLEMENTATION_GUIDE.md#full-i-016)** — Integration · Full · P0 — Full backup kinds and verification.

<a id="full-i-017"></a>

- [ ] **[FULL-I-017](TEST_IMPLEMENTATION_GUIDE.md#full-i-017)** — Integration · Full · P0 — Full database and secrets restore.

<a id="full-i-018"></a>

- [ ] **[FULL-I-018](TEST_IMPLEMENTATION_GUIDE.md#full-i-018)** — Integration · Full · P0 — PostgreSQL point-in-time recovery.

<a id="full-i-019"></a>

- [ ] **[FULL-I-019](TEST_IMPLEMENTATION_GUIDE.md#full-i-019)** — Integration · Full · P0 — Backup recovery failure containment.

<a id="full-i-020"></a>

- [ ] **[FULL-I-020](TEST_IMPLEMENTATION_GUIDE.md#full-i-020)** — Integration · Full · P1 — Concurrent distributed workers.

<a id="full-i-021"></a>

- [ ] **[FULL-I-021](TEST_IMPLEMENTATION_GUIDE.md#full-i-021)** — Integration · Full · P0 — Full package fresh boot and restart.

<a id="full-i-022"></a>

- [ ] **[FULL-I-022](TEST_IMPLEMENTATION_GUIDE.md#full-i-022)** — Integration · Full · P1 — Full transport/storage origin configuration.


<a id="suite-lite-u"></a>

### Backend Lite — unit tests

<a id="lite-u-001"></a>

- [ ] **[LITE-U-001](TEST_IMPLEMENTATION_GUIDE.md#lite-u-001)** — Unit · Lite · P0 — Local Admin identity.

<a id="lite-u-002"></a>

- [ ] **[LITE-U-002](TEST_IMPLEMENTATION_GUIDE.md#lite-u-002)** — Unit · Lite · P0 — Central Lite authorization.

<a id="lite-u-003"></a>

- [ ] **[LITE-U-003](TEST_IMPLEMENTATION_GUIDE.md#lite-u-003)** — Unit · Lite · P0 — Lite configuration selects local adapters.

<a id="lite-u-004"></a>

- [ ] **[LITE-U-004](TEST_IMPLEMENTATION_GUIDE.md#lite-u-004)** — Unit · Lite · P1 — Local queue capacity and cancellation rules.

<a id="lite-u-005"></a>

- [ ] **[LITE-U-005](TEST_IMPLEMENTATION_GUIDE.md#lite-u-005)** — Unit · Lite · P1 — SQLite value encoding.

<a id="lite-u-006"></a>

- [ ] **[LITE-U-006](TEST_IMPLEMENTATION_GUIDE.md#lite-u-006)** — Unit · Lite · P1 — Lucene document/query translation.

<a id="lite-u-007"></a>

- [ ] **[LITE-U-007](TEST_IMPLEMENTATION_GUIDE.md#lite-u-007)** — Unit · Lite · P0 — Lite recovery command validation.

<a id="lite-u-008"></a>

- [ ] **[LITE-U-008](TEST_IMPLEMENTATION_GUIDE.md#lite-u-008)** — Unit · Lite · P0 — Lite backup capability rejection.


<a id="suite-lite-i"></a>

### Backend Lite — integration tests

<a id="lite-i-001"></a>

- [ ] **[LITE-I-001](TEST_IMPLEMENTATION_GUIDE.md#lite-i-001)** — Integration · Lite · P0 — One-host composition and removed-service canary.

<a id="lite-i-002"></a>

- [ ] **[LITE-I-002](TEST_IMPLEMENTATION_GUIDE.md#lite-i-002)** — Integration · Lite · P0 — Local Admin seeding and restart.

<a id="lite-i-003"></a>

- [ ] **[LITE-I-003](TEST_IMPLEMENTATION_GUIDE.md#lite-i-003)** — Integration · Lite · P0 — SQLite configuration and contention.

<a id="lite-i-004"></a>

- [ ] **[LITE-I-004](TEST_IMPLEMENTATION_GUIDE.md#lite-i-004)** — Integration · Lite · P0 — Durable local inbox/outbox restart.

<a id="lite-i-005"></a>

- [ ] **[LITE-I-005](TEST_IMPLEMENTATION_GUIDE.md#lite-i-005)** — Integration · Lite · P0 — Durable lease expiry and stale owner fencing.

<a id="lite-i-006"></a>

- [ ] **[LITE-I-006](TEST_IMPLEMENTATION_GUIDE.md#lite-i-006)** — Integration · Lite · P0 — Abrupt process death during active download.

<a id="lite-i-007"></a>

- [ ] **[LITE-I-007](TEST_IMPLEMENTATION_GUIDE.md#lite-i-007)** — Integration · Lite · P1 — Local pub/sub and request/reply lifecycle.

<a id="lite-i-008"></a>

- [ ] **[LITE-I-008](TEST_IMPLEMENTATION_GUIDE.md#lite-i-008)** — Integration · Lite · P0 — Encrypted secrets persistence.

<a id="lite-i-009"></a>

- [ ] **[LITE-I-009](TEST_IMPLEMENTATION_GUIDE.md#lite-i-009)** — Integration · Lite · P0 — Secret key rotation and missing/corrupt keys.

<a id="lite-i-010"></a>

- [ ] **[LITE-I-010](TEST_IMPLEMENTATION_GUIDE.md#lite-i-010)** — Integration · Lite · P1 — Staged object lifecycle.

<a id="lite-i-011"></a>

- [ ] **[LITE-I-011](TEST_IMPLEMENTATION_GUIDE.md#lite-i-011)** — Integration · Lite · P0 — Embedded Lucene lifecycle.

<a id="lite-i-012"></a>

- [ ] **[LITE-I-012](TEST_IMPLEMENTATION_GUIDE.md#lite-i-012)** — Integration · Lite · P0 — Missing/corrupt index recovery.

<a id="lite-i-013"></a>

- [ ] **[LITE-I-013](TEST_IMPLEMENTATION_GUIDE.md#lite-i-013)** — Integration · Lite · P0 — Snapshot restore invalidates stale search.

<a id="lite-i-014"></a>

- [ ] **[LITE-I-014](TEST_IMPLEMENTATION_GUIDE.md#lite-i-014)** — Integration · Lite · P0 — Online SQLite snapshot integrity.

<a id="lite-i-015"></a>

- [ ] **[LITE-I-015](TEST_IMPLEMENTATION_GUIDE.md#lite-i-015)** — Integration · Lite · P0 — Offline Lite restore round trip.

<a id="lite-i-016"></a>

- [ ] **[LITE-I-016](TEST_IMPLEMENTATION_GUIDE.md#lite-i-016)** — Integration · Lite · P0 — Lite recovery failure containment.

<a id="lite-i-017"></a>

- [ ] **[LITE-I-017](TEST_IMPLEMENTATION_GUIDE.md#lite-i-017)** — Integration · Lite · P1 — SQLite maintenance and schedule persistence.

<a id="lite-i-018"></a>

- [ ] **[LITE-I-018](TEST_IMPLEMENTATION_GUIDE.md#lite-i-018)** — Integration · Lite · P0 — Local processor storage pipeline.

<a id="lite-i-019"></a>

- [ ] **[LITE-I-019](TEST_IMPLEMENTATION_GUIDE.md#lite-i-019)** — Integration · Lite · P0 — Lite package fresh boot, visible data and restart.

<a id="lite-i-020"></a>

- [ ] **[LITE-I-020](TEST_IMPLEMENTATION_GUIDE.md#lite-i-020)** — Integration · Lite · P1 — Low disk, denied writes and shutdown.


<a id="suite-ui-u"></a>

### Frontend supporting unit tests

<a id="ui-u-001"></a>

- [ ] **[UI-U-001](TEST_IMPLEMENTATION_GUIDE.md#ui-u-001)** — Unit · Both · P0 — Capability and permission helpers.

<a id="ui-u-002"></a>

- [ ] **[UI-U-002](TEST_IMPLEMENTATION_GUIDE.md#ui-u-002)** — Unit · Both · P0 — Layout session and return-path decisions.

<a id="ui-u-003"></a>

- [ ] **[UI-U-003](TEST_IMPLEMENTATION_GUIDE.md#ui-u-003)** — Unit · Both · P0 — API fetch, CSRF refresh and errors.

<a id="ui-u-004"></a>

- [ ] **[UI-U-004](TEST_IMPLEMENTATION_GUIDE.md#ui-u-004)** — Unit · Both · P1 — Frontend job-state reducers.

<a id="ui-u-005"></a>

- [ ] **[UI-U-005](TEST_IMPLEMENTATION_GUIDE.md#ui-u-005)** — Unit · Both · P1 — Options editor tri-state and serialization.

<a id="ui-u-006"></a>

- [ ] **[UI-U-006](TEST_IMPLEMENTATION_GUIDE.md#ui-u-006)** — Unit · Both · P1 — Chat playback time and rendering data.


<a id="suite-ui-i"></a>

### Frontend supporting component integration tests

<a id="ui-i-001"></a>

- [ ] **[UI-I-001](TEST_IMPLEMENTATION_GUIDE.md#ui-i-001)** — Integration · Both · P1 — Backup component capability variants.

<a id="ui-i-002"></a>

- [ ] **[UI-I-002](TEST_IMPLEMENTATION_GUIDE.md#ui-i-002)** — Integration · Both · P1 — Shared form and dialog integration.


<a id="suite-ui-core-e"></a>

### Frontend Core — E2E against both editions

<a id="ui-core-e-001"></a>

- [ ] **[UI-CORE-E-001](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-001)** — E2E · Full + Lite · P0 — Application boot and primary navigation.

<a id="ui-core-e-002"></a>

- [ ] **[UI-CORE-E-002](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-002)** — E2E · Full + Lite · P0 — Deep links, reload and history.

<a id="ui-core-e-003"></a>

- [ ] **[UI-CORE-E-003](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-003)** — E2E · Full + Lite · P1 — Empty library and first-use state.

<a id="ui-core-e-004"></a>

- [ ] **[UI-CORE-E-004](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-004)** — E2E · Full + Lite · P1 — Library filtering, sorting and pagination.

<a id="ui-core-e-005"></a>

- [ ] **[UI-CORE-E-005](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-005)** — E2E · Full + Lite · P1 — Creator and channel browsing.

<a id="ui-core-e-006"></a>

- [ ] **[UI-CORE-E-006](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-006)** — E2E · Full + Lite · P0 — Single video download happy path.

<a id="ui-core-e-007"></a>

- [ ] **[UI-CORE-E-007](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-007)** — E2E · Full + Lite · P1 — Download form rejection and recovery.

<a id="ui-core-e-008"></a>

- [ ] **[UI-CORE-E-008](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-008)** — E2E · Full + Lite · P1 — Playlist/channel download and monitoring.

<a id="ui-core-e-009"></a>

- [ ] **[UI-CORE-E-009](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-009)** — E2E · Full + Lite · P0 — Job controls, stop and restart.

<a id="ui-core-e-010"></a>

- [ ] **[UI-CORE-E-010](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-010)** — E2E · Full + Lite · P1 — Job failure, history and cleanup.

<a id="ui-core-e-011"></a>

- [ ] **[UI-CORE-E-011](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-011)** — E2E · Full + Lite · P1 — Background and encoding job views.

<a id="ui-core-e-012"></a>

- [ ] **[UI-CORE-E-012](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-012)** — E2E · Full + Lite · P0 — Original video playback controls.

<a id="ui-core-e-013"></a>

- [ ] **[UI-CORE-E-013](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-013)** — E2E · Full + Lite · P0 — HLS generation and rendition playback.

<a id="ui-core-e-014"></a>

- [ ] **[UI-CORE-E-014](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-014)** — E2E · Full + Lite · P1 — Audio playback and channel podcast.

<a id="ui-core-e-015"></a>

- [ ] **[UI-CORE-E-015](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-015)** — E2E · Full + Lite · P1 — Caption selection and transcript navigation.

<a id="ui-core-e-016"></a>

- [ ] **[UI-CORE-E-016](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-016)** — E2E · Full + Lite · P1 — Watch metadata, versions and comments.

<a id="ui-core-e-017"></a>

- [ ] **[UI-CORE-E-017](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-017)** — E2E · Full + Lite · P0 — Resume position and watched history.

<a id="ui-core-e-018"></a>

- [ ] **[UI-CORE-E-018](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-018)** — E2E · Full + Lite · P1 — Likes and saved playlists from watch.

<a id="ui-core-e-019"></a>

- [ ] **[UI-CORE-E-019](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-019)** — E2E · Full + Lite · P1 — Personal playlists CRUD and ordering.

<a id="ui-core-e-020"></a>

- [ ] **[UI-CORE-E-020](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-020)** — E2E · Full + Lite · P1 — Notes on media and creators.

<a id="ui-core-e-021"></a>

- [ ] **[UI-CORE-E-021](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-021)** — E2E · Full + Lite · P0 — Basic search with real index.

<a id="ui-core-e-022"></a>

- [ ] **[UI-CORE-E-022](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-022)** — E2E · Full + Lite · P1 — Advanced search and query validation.

<a id="ui-core-e-023"></a>

- [ ] **[UI-CORE-E-023](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-023)** — E2E · Full + Lite · P0 — Search lifecycle and outage UI.

<a id="ui-core-e-024"></a>

- [ ] **[UI-CORE-E-024](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-024)** — E2E · Full + Lite · P1 — Local storage administration.

<a id="ui-core-e-025"></a>

- [ ] **[UI-CORE-E-025](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-025)** — E2E · Full + Lite · P1 — Network and object storage forms.

<a id="ui-core-e-026"></a>

- [ ] **[UI-CORE-E-026](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-026)** — E2E · Full + Lite · P0 — Import wizard complete flow.

<a id="ui-core-e-027"></a>

- [ ] **[UI-CORE-E-027](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-027)** — E2E · Full + Lite · P1 — Import resume, invalid files and failed-item retry.

<a id="ui-core-e-028"></a>

- [ ] **[UI-CORE-E-028](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-028)** — E2E · Full + Lite · P1 — Import browse and destructive-action boundaries.

<a id="ui-core-e-029"></a>

- [ ] **[UI-CORE-E-029](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-029)** — E2E · Full + Lite · P1 — Schedule administration.

<a id="ui-core-e-030"></a>

- [ ] **[UI-CORE-E-030](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-030)** — E2E · Full + Lite · P1 — Metadata administration and deletion.

<a id="ui-core-e-031"></a>

- [ ] **[UI-CORE-E-031](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-031)** — E2E · Full + Lite · P1 — Worker administration.

<a id="ui-core-e-032"></a>

- [ ] **[UI-CORE-E-032](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-032)** — E2E · Full + Lite · P1 — Statistics display.

<a id="ui-core-e-033"></a>

- [ ] **[UI-CORE-E-033](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-033)** — E2E · Full + Lite · P0 — Shared backup creation and basic verification.

<a id="ui-core-e-034"></a>

- [ ] **[UI-CORE-E-034](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-034)** — E2E · Full + Lite · P1 — Cookie profile management.

<a id="ui-core-e-035"></a>

- [ ] **[UI-CORE-E-035](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-035)** — E2E · Full + Lite · P1 — Option preset management.

<a id="ui-core-e-036"></a>

- [ ] **[UI-CORE-E-036](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-036)** — E2E · Full + Lite · P1 — Download config set management.

<a id="ui-core-e-037"></a>

- [ ] **[UI-CORE-E-037](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-037)** — E2E · Full + Lite · P1 — Notifications and provider management.

<a id="ui-core-e-038"></a>

- [ ] **[UI-CORE-E-038](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-038)** — E2E · Full + Lite · P1 — Appearance, theme and custom CSS.

<a id="ui-core-e-039"></a>

- [ ] **[UI-CORE-E-039](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-039)** — E2E · Full + Lite · P1 — Error pages and recovery.

<a id="ui-core-e-040"></a>

- [ ] **[UI-CORE-E-040](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-040)** — E2E · Full + Lite · P1 — SSE reconnect and navigation cleanup.

<a id="ui-core-e-041"></a>

- [ ] **[UI-CORE-E-041](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-041)** — E2E · Full + Lite · P1 — Keyboard and accessibility journeys.

<a id="ui-core-e-042"></a>

- [ ] **[UI-CORE-E-042](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-042)** — E2E · Full + Lite · P1 — Responsive layouts and touch.

<a id="ui-core-e-043"></a>

- [ ] **[UI-CORE-E-043](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-043)** — E2E · Full + Lite · P1 — Untrusted content rendering.

<a id="ui-core-e-044"></a>

- [ ] **[UI-CORE-E-044](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-044)** — E2E · Full + Lite · P1 — Rapid interaction and mutation recovery.

<a id="ui-core-e-045"></a>

- [ ] **[UI-CORE-E-045](TEST_IMPLEMENTATION_GUIDE.md#ui-core-e-045)** — E2E · Full + Lite · P1 — Localized data and time boundaries.


<a id="suite-ui-full-e"></a>

### Frontend Full — E2E

<a id="ui-full-e-001"></a>

- [ ] **[UI-FULL-E-001](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-001)** — E2E · Full · P0 — Real Authentik sign-in and return location.

<a id="ui-full-e-002"></a>

- [ ] **[UI-FULL-E-002](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-002)** — E2E · Full · P0 — Sign-out and revoked session.

<a id="ui-full-e-003"></a>

- [ ] **[UI-FULL-E-003](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-003)** — E2E · Full · P0 — Expiry and refresh during use.

<a id="ui-full-e-004"></a>

- [ ] **[UI-FULL-E-004](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-004)** — E2E · Full · P0 — Permission-aware navigation and direct links.

<a id="ui-full-e-005"></a>

- [ ] **[UI-FULL-E-005](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-005)** — E2E · Full · P0 — Private data across two browser users.

<a id="ui-full-e-006"></a>

- [ ] **[UI-FULL-E-006](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-006)** — E2E · Full · P1 — Access bundles CRUD.

<a id="ui-full-e-007"></a>

- [ ] **[UI-FULL-E-007](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-007)** — E2E · Full · P0 — Access policies and effective permissions.

<a id="ui-full-e-008"></a>

- [ ] **[UI-FULL-E-008](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-008)** — E2E · Full · P1 — Legacy access-management redirects.

<a id="ui-full-e-009"></a>

- [ ] **[UI-FULL-E-009](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-009)** — E2E · Full · P0 — Browser CSRF token recovery.

<a id="ui-full-e-010"></a>

- [ ] **[UI-FULL-E-010](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-010)** — E2E · Full · P1 — Full backup options.

<a id="ui-full-e-011"></a>

- [ ] **[UI-FULL-E-011](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-011)** — E2E · Full · P0 — Restore console full backup.

<a id="ui-full-e-012"></a>

- [ ] **[UI-FULL-E-012](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-012)** — E2E · Full · P0 — Restore console PITR.

<a id="ui-full-e-013"></a>

- [ ] **[UI-FULL-E-013](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-013)** — E2E · Full · P1 — Full security dependency outage UI.

<a id="ui-full-e-014"></a>

- [ ] **[UI-FULL-E-014](TEST_IMPLEMENTATION_GUIDE.md#ui-full-e-014)** — E2E · Full · P1 — HTTPS session and cross-origin media behavior.


<a id="suite-ui-lite-e"></a>

### Frontend Lite — E2E

<a id="ui-lite-e-001"></a>

- [ ] **[UI-LITE-E-001](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-001)** — E2E · Lite · P0 — Immediate local Admin session.

<a id="ui-lite-e-002"></a>

- [ ] **[UI-LITE-E-002](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-002)** — E2E · Lite · P0 — Access-management controls unavailable.

<a id="ui-lite-e-003"></a>

- [ ] **[UI-LITE-E-003](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-003)** — E2E · Lite · P0 — Shared admin features stay available.

<a id="ui-lite-e-004"></a>

- [ ] **[UI-LITE-E-004](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-004)** — E2E · Lite · P0 — Lite backup options and help.

<a id="ui-lite-e-005"></a>

- [ ] **[UI-LITE-E-005](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-005)** — E2E · Lite · P0 — Lite failure does not launch external login.

<a id="ui-lite-e-006"></a>

- [ ] **[UI-LITE-E-006](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-006)** — E2E · Lite · P0 — Lite restart preserves user workflow.

<a id="ui-lite-e-007"></a>

- [ ] **[UI-LITE-E-007](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-007)** — E2E · Lite · P0 — Browser after offline restore.

<a id="ui-lite-e-008"></a>

- [ ] **[UI-LITE-E-008](TEST_IMPLEMENTATION_GUIDE.md#ui-lite-e-008)** — E2E · Lite · P1 — Lucene recovery visible in the browser.


<a id="suite-ui-opt-e"></a>

### Frontend optional integrations and device E2E

<a id="ui-opt-e-001"></a>

- [ ] **[UI-OPT-E-001](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-001)** — E2E · Both; explicit feature/device matrix · P1 — Live chat disabled.

<a id="ui-opt-e-002"></a>

- [ ] **[UI-OPT-E-002](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-002)** — E2E · Both; explicit feature/device matrix · P1 — Live chat replay enabled.

<a id="ui-opt-e-003"></a>

- [ ] **[UI-OPT-E-003](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-003)** — E2E · Both; explicit feature/device matrix · P1 — Live chat outage and recovery.

<a id="ui-opt-e-004"></a>

- [ ] **[UI-OPT-E-004](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-004)** — E2E · Both; explicit feature/device matrix · P1 — POT enabled and unavailable.

<a id="ui-opt-e-005"></a>

- [ ] **[UI-OPT-E-005](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-005)** — E2E · Both; explicit feature/device matrix · P2 — Server-managed casting to a receiver.

<a id="ui-opt-e-006"></a>

- [ ] **[UI-OPT-E-006](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-006)** — E2E · Both; explicit feature/device matrix · P2 — Browser casting on supported hardware.

<a id="ui-opt-e-007"></a>

- [ ] **[UI-OPT-E-007](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-007)** — E2E · Both; explicit feature/device matrix · P1 — No casting support or no receivers.

<a id="ui-opt-e-008"></a>

- [ ] **[UI-OPT-E-008](TEST_IMPLEMENTATION_GUIDE.md#ui-opt-e-008)** — E2E · Both; explicit feature/device matrix · P1 — Optional feature combination smoke.


<a id="suite-accept-i"></a>

### System acceptance exercised as integration tests

<a id="accept-i-001"></a>

- [ ] **[ACCEPT-I-001](TEST_IMPLEMENTATION_GUIDE.md#accept-i-001)** — Integration · Both; recorded environment matrix · P0 — Solution and dependency boundaries after relocation.

<a id="accept-i-002"></a>

- [ ] **[ACCEPT-I-002](TEST_IMPLEMENTATION_GUIDE.md#accept-i-002)** — Integration · Both; recorded environment matrix · P1 — Packaging and schema tooling regression.

<a id="accept-i-003"></a>

- [ ] **[ACCEPT-I-003](TEST_IMPLEMENTATION_GUIDE.md#accept-i-003)** — Integration · Both; recorded environment matrix · P2 — Reference corpus indexing and latency.

<a id="accept-i-004"></a>

- [ ] **[ACCEPT-I-004](TEST_IMPLEMENTATION_GUIDE.md#accept-i-004)** — Integration · Both; recorded environment matrix · P2 — Sustained combined workload.

<a id="accept-i-005"></a>

- [ ] **[ACCEPT-I-005](TEST_IMPLEMENTATION_GUIDE.md#accept-i-005)** — Integration · Both; recorded environment matrix · P2 — Reference-machine startup and idle resources.

<a id="accept-i-006"></a>

- [ ] **[ACCEPT-I-006](TEST_IMPLEMENTATION_GUIDE.md#accept-i-006)** — Integration · Both; recorded environment matrix · P1 — External provider acquisition compatibility.

<a id="accept-i-007"></a>

- [ ] **[ACCEPT-I-007](TEST_IMPLEMENTATION_GUIDE.md#accept-i-007)** — Integration · Both; recorded environment matrix · P1 — Test isolation, cleanup and meaningful discovery.

