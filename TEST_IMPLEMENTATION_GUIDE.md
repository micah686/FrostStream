# FrostStream test implementation guide

This guide implements the acceptance plan in [TEST_CHECKLISTS.md](TEST_CHECKLISTS.md). Every test specification below links back to its checklist entry. It covers backend unit/integration testing of Core, Full and Lite, and frontend E2E testing with shared and edition-specific suites. It is a plan: no test results are asserted and no tests or automation are installed by creating these documents.

**GitHub Actions is deferred.** Develop and run these suites locally first. Do not create, edit, enable or expand workflow files as part of this plan. Existing `.github/workflows` are outside this documentation change. Even after all gates pass, wait for the user's explicit request before adding Actions.

## Navigation

- [Baseline and existing coverage](#repository-baseline-and-sources-of-truth)
- [Edition contract](#edition-and-feature-contract), [organization](#test-organization-and-identity) and [requirements/fixtures](#requirements-and-fixture-recipes)
- [Frontend E2E harness](#frontend-e2e-harness) and [implementation sequence](#implementation-sequence)
- [Local commands](#running-and-validating-locally) and [completion/evidence](#completion-and-evidence)
- [Frontend route map](#frontend-route-coverage-map) and [backend controller map](#backend-http-surface-coverage-map)

| Test specifications | Checklist |
| --- | --- |
| [Backend Core — unit tests](#suite-core-u) | [CORE-U](TEST_CHECKLISTS.md#suite-core-u) |
| [Backend Core — integration contracts](#suite-core-i) | [CORE-I](TEST_CHECKLISTS.md#suite-core-i) |
| [Backend Full — unit tests](#suite-full-u) | [FULL-U](TEST_CHECKLISTS.md#suite-full-u) |
| [Backend Full — integration tests](#suite-full-i) | [FULL-I](TEST_CHECKLISTS.md#suite-full-i) |
| [Backend Lite — unit tests](#suite-lite-u) | [LITE-U](TEST_CHECKLISTS.md#suite-lite-u) |
| [Backend Lite — integration tests](#suite-lite-i) | [LITE-I](TEST_CHECKLISTS.md#suite-lite-i) |
| [Frontend supporting unit tests](#suite-ui-u) | [UI-U](TEST_CHECKLISTS.md#suite-ui-u) |
| [Frontend supporting component integration tests](#suite-ui-i) | [UI-I](TEST_CHECKLISTS.md#suite-ui-i) |
| [Frontend Core — E2E against both editions](#suite-ui-core-e) | [UI-CORE-E](TEST_CHECKLISTS.md#suite-ui-core-e) |
| [Frontend Full — E2E](#suite-ui-full-e) | [UI-FULL-E](TEST_CHECKLISTS.md#suite-ui-full-e) |
| [Frontend Lite — E2E](#suite-ui-lite-e) | [UI-LITE-E](TEST_CHECKLISTS.md#suite-ui-lite-e) |
| [Frontend optional integrations and device E2E](#suite-ui-opt-e) | [UI-OPT-E](TEST_CHECKLISTS.md#suite-ui-opt-e) |
| [System acceptance exercised as integration tests](#suite-accept-i) | [ACCEPT-I](TEST_CHECKLISTS.md#suite-accept-i) |

## Repository baseline and sources of truth

The current projects use .NET 10, TUnit on Microsoft.Testing.Platform, Shouldly, NSubstitute, ASP.NET Core testing and Testcontainers. Versions come from [global.json](global.json), [Directory.Packages.props](Directory.Packages.props), the [unit project](src/Tests/UnitTests/UnitTests.csproj) and [integration project](src/Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj). Retain the selected runner and central package management. The integration executable has a custom `LiteCrashHost` entry point that delegates normal runs to the generated runner; preserve it.

The frontend is SvelteKit with a static fallback, Node >=22 and pnpm 10.28.1 declared in [package.json](src/Frontend/package.json). It has Node access tests and actual Svelte backup-render tests. There is no repository Playwright Test configuration/dependency at this baseline. The standalone [Authentik browser checker](src/tooling/installation/check-authentik.mjs) and [Full media checker](src/tooling/installation/check-full-media.mjs) are useful starting points, not a complete frontend suite. The backup-render test is a component integration test; the Lite HTTP runtime suite is backend integration, even where its fixture names say E2E.

| Existing area | Reuse and audit |
| --- | --- |
| `src/Tests/UnitTests/Worker`, `WebAPI`, `Storage`, `Scheduler`, `Backups`, `Discovery`, `Imports`, `Notifications` | Map individual methods to checklist IDs. Split pure fakes from real filesystem/database/process work. |
| `src/Tests/UnitTests/DataBridge`, `Search`, `Deployment` | Substantial real persistence/search/runtime coverage exists here. Classify by exercised boundary, then relocate as part of test implementation without losing discovery or assertions. |
| `src/Tests/IntegrationTests/IntegrationTests/{Auth,Storage,DataBridge,Cookies,Maintenance,WebAPI}` | Full fixtures cover selected real services. Controlled JWT tests prove resource authorization, not real Authentik browser login. |
| `src/Tests/IntegrationTests/IntegrationTests/Lite` | Reuse real loopback host, generated FFmpeg media, temporary SQLite/secrets/search/storage and crash-host barriers. Acquisition and binary provisioning are currently replaced. |
| `src/Frontend/tests/*.test.mjs` | Extend access/layout helpers and real backup component rendering; keep these separately reported from E2E. |
| `src/tooling/{architecture,installation,recovery,performance}` | Reuse explicit invariants and resource measurements after auditing moved-root assumptions and prerequisites. Historical JSON is evidence from its recorded run only. |

Important gaps to address before claiming coverage:

1. Some PostgreSQL/Typesense/benchmark tests are environment-dependent and can omit work or return early when configuration is missing. Required profiles must fail preflight or report an explicit blocked/skip result. Audit actual executed variants, not only the runner's green summary.
2. `DataBridgeTestHelpers` uses EF InMemory for some tests. This can support isolated consumer behavior but cannot establish SQL translation, transactions, indexes, constraints, locking or provider parity.
3. Several container fixtures currently use `latest` tags. Pin fixture versions/digests when implementing reproducible service profiles and record those versions per run.
4. Older README commands omit `src/`. Some Python tools derive their root as `src/` while central props, `global.json` and the solution are at the repository root. The architecture/package harness work must correct and validate these assumptions before its commands are considered reproducible. Do not blindly copy old paths or claim those tools currently pass.
5. [Historical validation](src/docs/verification/current-validation.md) records Full media-transfer/HLS failures. Current source now has a `MediaProcessorStorage` policy and sends `X-FrostStream-Service-Token`; this is a regression target to execute, not proof the historical failure still exists. WebAPI uses `MediaProcessorStorage:Token`; the processor uses `MediaProcessor:StorageApiToken`.
6. Current source contains schema evolution and [upgrade tests](src/Tests/UnitTests/DataBridge/PersistenceUpgradeTests.cs), despite older prose describing only fresh installs. Derive supported upgrade versions from current initializers/history. Preserve immutable historical baselines; do not manufacture support for arbitrary old databases.
7. The [Admin navigation](src/Frontend/src/routes/admin/+layout.svelte) links to `/admin/backups`, but no page exists at that route in this checkout. `BackupsSection.svelte` is exercised by its render test and is not mounted by a current page. The shared backup browser case must expose this gap; completing it requires implementing/restoring the page before claiming end-to-end coverage. `/admin` itself redirects to `/admin/storage`.

## Edition and feature contract

Use [SystemCapabilities.cs](src/Shared/Core/Deployment/SystemCapabilities.cs), [frontendAccess.ts](src/Frontend/src/lib/frontendAccess.ts) and each composition root as the oracle. Capabilities declare configured availability; `/api/system/integrations` reports runtime state. An unavailable dependency must not silently change Full into Lite or erase a configured feature.

| Concern | Core/shared behavior | Full | Lite |
| --- | --- | --- | --- |
| Main product | Download, import, library/creators, media processing/playback, search, jobs, personal features, storage, schedules, statistics | Shared behavior with distributed hosts | Shared behavior in one host |
| Identity | Ownership follows authenticated principal | Authentik OIDC/BFF, JWT validation, OpenFGA permissions | Fixed local Admin, central permissive product authorization |
| Access management | Validate relevant frontend/backend decisions | Enabled, bundles/policies/effective access and user isolation | Disabled; hide UI and redirect Full-only routes |
| Persistence | Identical shared repository semantics | PostgreSQL | SQLite |
| Messaging/secrets | Durable effects, cancellation and secret isolation | NATS / OpenBao | Local durable SQLite transport / encrypted local secrets |
| Search | Typed search, indexing, rebuild and truthful status | Typesense | Embedded Lucene |
| Backups | List jobs, full backup, basic verification | PostgreSQL full/differential/incremental, deep verification, PITR, separate restore console and paired secrets | SQLite full snapshot/basic verification, offline recovery into a new directory; no differential/incremental/deep/PITR |
| Live chat | Optional `LiveChat:Enabled`, replay and clear degraded state | ClickHouse when enabled | ClickHouse when enabled; therefore optional Lite profile may use a container |
| POT/enrichment | Optional, bounded failure with base workflow intact | Configure POT broker/provider explicitly | Configure POT broker/provider explicitly |
| Casting/storage | Advertised features are not inherently Full-only | Test actual capabilities/protocols | Test actual capabilities/protocols |

Full tests require anonymous, admin, allowed reader, denied user and two distinct ordinary users. Include two admins for private-resource isolation: being an admin must not imply ownership of another user's playlists/cookies/notes. Lite has one intended owner, so do not invent multi-user Lite authorization requirements. Hiding a control never replaces Full API authorization tests.

## Test organization and identity

Keep the two existing backend test projects initially. Proposed destinations, created during implementation:

```text
src/Tests/UnitTests/{Core,Full,Lite}/<Feature>/<Feature>Tests.cs
src/Tests/IntegrationTests/IntegrationTests/{Core,Full,Lite}/<Feature>/<Feature>Tests.cs
src/Tests/IntegrationTests/IntegrationTests/Infrastructure/<Fixture>.cs
src/Frontend/tests/unit/<feature>.test.mjs
src/Frontend/tests/integration/<feature>.test.mjs
src/Frontend/tests/e2e/{core,full,lite,optional}/<feature>.spec.ts
src/Frontend/tests/e2e/fixtures/<fixture>.ts
src/Frontend/playwright.config.ts
.artifacts/testing/<run-id>/{manifest.json,results,logs,traces,screenshots}
```

These locations and the frontend E2E commands below are **proposed**, not currently installed. Do not rename production namespaces to match the testing plan. Existing test classes may be extended in place first, with explicit mapping in the run manifest. Eventually move real integration cases out of `UnitTests` while preserving their behavior.

Include the ID in new backend test method names using underscores (`CORE_U_001_RejectsUnknownMode`) and in browser test titles (`[UI-CORE-E-006] download completes`). Add edition/kind/feature metadata using APIs supported by the installed TUnit version. A manifest maps existing unchanged method names to IDs. One parameterized method may satisfy multiple named input variants of one ID; do not count an entire class as covered without inspecting its assertions. Additional regressions get new IDs and matching entries in both documents.

## Requirements and fixture recipes

Every case below names a recipe. Its requirements, isolation rules, implementation location and evidence requirements are inherited by that case; the case's implementation/validation text supplies its extra data and dependencies.

| Recipe | Requirements and construction | Destination and execution |
| --- | --- | --- |
| `U` | Existing .NET SDK/TUnit/Shouldly/NSubstitute; fake `NodaTime.IClock`/time abstraction, task barriers and substitute contract interfaces. No real DB/network/FFmpeg. If a proposed case must touch these, split out an integration method. | Backend `UnitTests/{Core,Full,Lite}` by ID prefix. Run selected method/class, then affected unit group. |
| `CI` | Shared integration harness with both production SQLite and PostgreSQL adapters for persistence; Lucene and Typesense for search. For composed HTTP/workflow cases run both edition compositions. Add real storage/FFmpeg/ClickHouse only as the case requires. | Backend `IntegrationTests/Core`; parameterized named provider/edition variants. |
| `FI` | Disposable Full service fixtures or production package: PostgreSQL, NATS, OpenFGA, OpenBao, Typesense as applicable; Authentik for OIDC; real distinct executable hosts for distributed workflows; FFmpeg/yt-dlp and pgBackRest/WAL for media/recovery cases. | Backend `IntegrationTests/Full`; separate lightweight service contracts, full composition, backup and failure profiles. |
| `LI` | Reuse Lite runtime fixture with real SQLite, local transport/secrets, Lucene and loopback HTTP. Own a fresh temp data root. Use real FFmpeg for processing. Keep crash child-process support. | Backend `IntegrationTests/Lite`; single process except explicit crash/restart cases. |
| `JU` | Existing Node test runner and production TypeScript helpers using the current loading pattern; fake fetch, clock/browser boundary; reset module globals per test. | Frontend `tests/unit`; preserve existing access script until migrated and mapped. |
| `JI` | Existing Vite/Svelte SSR rendering or a real browser component harness as needed for interaction; real components with controlled adapters. | Frontend `tests/integration`; retain existing backup render tests. Mocks must be disclosed. |
| `EC` | Playwright Test plus the real frontend/proxy and real Full or Lite fixture; immutable known media seed, real API, test-owned users/storage. Run every case on both. | `tests/e2e/core`; `core-full-chromium` and `core-lite-chromium`, plus compatibility subset projects. |
| `EF` | `EC` with real Full services and distinct Authentik browser identities; separate recovery installation/console for restore cases. | `tests/e2e/full`; `full-chromium` and applicable compatibility projects. |
| `EL` | `EC` with real Lite host, initially empty browser storage and no external auth. Recovery/index mutations use an exclusively owned fixture. | `tests/e2e/lite`; `lite-chromium` and applicable compatibility projects. |
| `EO` | `EC` in both editions with explicit live-chat/POT off/on/outage profiles; ClickHouse or controlled external service when needed. Hardware/browser support is mandatory for real browser casting. | `tests/e2e/optional`; name edition/feature/device variants. Lack of prerequisites leaves that variant blocked. |
| `A` | Architecture/package tooling plus fresh disposable installations or a dedicated measured hardware fixture. Python/Compose/Aspire needed where relevant; corpus/duration from acceptance.json. | Integration/acceptance runner under existing tooling or integration project; record every environment and raw measurement. |

Use a Docker-compatible engine for Testcontainers. Rootless Podman setup may require a correct `DOCKER_HOST`; if disabling Ryuk is necessary, implement reliable explicit cleanup and record the configuration. Package startup requires a Compose provider that implements completed-initializer dependencies. Verify the installed Aspire/package requirements from current project/tool files rather than assuming historical README versions. Pin new Playwright dependencies/browser revisions when implementation begins and retain the frontend lockfile. Install required browsers with that pinned package.

### Isolation, seeding and fault control

Assign a unique run ID and per-test resource prefix. Each run owns its database/schema, NATS stream/consumer names, OpenFGA store/model, OpenBao namespace/path or instance, Typesense collections/aliases, temp files, ports, containers and browser identities. A fixture ownership manifest must be checked before destructive cleanup, process termination or restore. Use disposable data under `.artifacts/testing/<run-id>` or a unique OS temp directory; never the checkout's `data/` or a developer's running installation. Restoration targets a distinct directory/volume. Tests must be able to run individually and in a different order.

Seed fixtures programmatically through supported APIs/services or explicit test-only builders, without adding production reset/auth-bypass endpoints. Wait for required schema, handlers and search readiness, then verify `/api/system/capabilities` is the requested edition/profile. A 200 homepage is not sufficient readiness. Do not swallow startup errors. Build a run manifest before tests and stop preflight when a required service is absent.

Use known values: two distinct videos with different hashes to avoid accidental deduplication, a short compatible audio fixture, multilingual captions, comments/chat with offsets, artwork and missing/corrupt variants, two creators, enough media for paging, one restricted media item and separate per-user private records. Generate media with real FFmpeg using a supported software codec and record duration/codecs/SHA-256. Freeze logical business time where practical and record UTC/timezone assumptions; use actual monotonic deadlines for I/O. Exclude live Internet from deterministic tests. A local HTTP origin can exercise actual yt-dlp without provider instability; separately label any fake acquisition, binary provisioning or notification/POT receiver.

Database assertions use a fresh connection after the operation and after restart when durability matters. Use real PostgreSQL/SQLite for provider semantics; EF InMemory is not a relational substitute. For search, assert known membership/filters and eventual convergence with a deadline, not identical engine scores. For media, HTTP 200 or a manifest string alone is insufficient: verify bytes/ranges/hash and decode outputs. For optional-service failure, assert no unbounded retries and continued healthy base workflows.

Use barriers, controllable origin responses, fixture-owned network proxies, controlled clocks and process lifecycle hooks to hit crash/retry windows deterministically. Do not use fixed sleeps as readiness assertions. Collect logs, run IDs, DB state, queue state and process exit status before cleanup on failure. Bound waits and poll the actual invariant; a timeout is a failure with diagnostics. Clean successful runs in `finally`/fixture teardown; record failed cleanup separately and retain only explicitly owned evidence needed for diagnosis.

## Frontend E2E harness

Implement a shared Playwright fixture layer for base URL, edition/capability assertions, seed helpers, owned resource cleanup, browser login, media origin, service fault controls and artifact capture. Use separate [Playwright projects](https://playwright.dev/docs/test-projects) for edition/browser combinations and explicit `testMatch` patterns. Core files should run once against each edition; Full/Lite files must only match their own projects.

Recommended initial project names are `core-lite-chromium`, `core-full-chromium`, `lite-chromium` and `full-chromium`. Add named Firefox/WebKit, phone (390×844), tablet (768×1024) and desktop (1440×900) projects for the required compatibility cases. Use a `@compat` tag on navigation, forms, login/logout, search, player/audio/captions/resume, dialogs and import cases. Run the complete suite in Chromium and the compatibility subset in the other browsers/viewports. Media support must be measured using the chosen browser build/codecs. Emulated mobile browsers do not certify physical Safari or casting hardware; record device acceptance separately.

For Full shared tests, create independent real OIDC-authenticated state for each mutation worker/user. Keep login/logout/refresh tests in fresh contexts so they test the actual authentication path. The [Playwright authentication guide](https://playwright.dev/docs/auth) describes isolated contexts and reusable state; saved state contains credentials and belongs only in ignored per-run artifacts. Never grant an ordinary-user fixture admin rights to make a case pass. Lite gets a clean context without fabricated auth cookies. Two-user tests use two independent contexts.

Exercise actual UI for the behavior under test. API seeding and post-action verification are allowed. Do not call the mutation API in place of clicking the UI action being certified. Do not fulfill FrostStream API calls with fake successes in these E2E suites. An API-mocked browser scenario belongs in frontend integration. Optional external origins/receivers may be controlled fixtures and must be named in the report. Casting claims distinguish a protocol-faithful receiver from actual hardware.

Prefer accessible roles/names and labels; introduce stable test IDs only when semantic locators cannot distinguish dynamic rows. Use [retrying Playwright assertions](https://playwright.dev/docs/test-assertions) for visible state and polling for persisted state. Capture unexpected page errors, failed relevant requests and non-allowlisted console errors. Use screenshots for diagnosis or narrowly reviewed visual baselines, not as the only proof of a mutation. Observe actual media `currentTime`, error state and network payloads for playback, with declared tolerances.

Each profile starts through the actual supported frontend proxy. The Vite development server proxies `/api`, `/auth` and `/stream`; bare `vite preview` does not inherit that proxy setup. E2E may use the configured dev proxy while developing tests; final acceptance must include the built static frontend through Caddy/package routing, direct reloads and the HTTPS Full profile. Keep API and auth cookies on the intended origin. Do not route all browser requests directly to backend HTTP and thereby omit proxy behavior.

Disable retries for completion evidence. On failure retain trace, screenshot, browser errors, relevant request statuses and server logs under the run ID. Evidence can contain session/secret data; keep raw auth state/traces private and ignored, and publish only sanitized summaries. A diagnostic rerun may help investigate flakiness but does not erase a first-run failure. Keep tests independent; quarantine leaves the checklist open with an owner/reason.

## Implementation sequence

1. **Foundation and audit:** map existing methods to IDs, fix test-tool path assumptions for `src/`, create the manifest/preflight/seed/cleanup harness, pin service fixtures, and make omitted providers visible. Establish unit/integration classification before moving files. Inventory actual API actions and frontend route exports so each maps to a case below.
2. **Core:** implement pure logic and real shared provider contracts. Complete schema initialization/upgrades, ownership, message durability, search activation, storage and download/import/playback invariants first. Then cover all shared features and failure paths.
3. **Lite:** extend the existing runtime/crash fixtures for local durability, encryption, recovery and zero required Full-service calls. This supplies a fast realistic backend for initial browser development.
4. **Full:** establish required service fixtures, real OIDC sessions and secure processor transfer, then separate-host media work, failure/restart, authorization, secret and backup recovery. Do not weaken authentication or substitute an in-process host to satisfy distributed acceptance.
5. **Frontend:** add the Playwright harness and Core browser cases against both editions, then edition-only/optional profiles and responsive/accessibility coverage. Bring standalone scripts into repeatable fixtures while retaining their proven checks.
6. **Extended acceptance:** run protocol/device cases, restore round trips, package/HTTPS smoke, reference corpus and sustained-resource measurements. Complete declared compatibility variants and isolated/mixed suite execution.
7. **Local sign-off:** reconcile results to every checklist ID and variant. Resolve failures/flakes and explicitly record externally blocked hardware/provider cases. The user decides when testing is done; Actions remains deferred until separately requested.

## Running and validating locally

The following existing commands use the current paths. They are execution instructions for the implementation effort and were not run to write this plan:

```sh
# From the repository root; restore/build before using --no-build.
dotnet restore FrostStream.slnx
dotnet build FrostStream.slnx --no-restore -m:1
dotnet run --no-build --project src/Tests/UnitTests/UnitTests.csproj -- --maximum-parallel-tests 4
dotnet run --no-build --project src/Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- --maximum-parallel-tests 4

# Existing focused Lite runtime selection (requires FFmpeg/FFprobe).
dotnet run --no-build --project src/Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- --treenode-filter '/*/IntegrationTests.Lite/*/*'

# Existing frontend checks, from src/Frontend.
cd src/Frontend
pnpm install --frozen-lockfile
pnpm test:access
pnpm test:backups
pnpm check
pnpm build
```

The current `UnitTests` command may execute real integration code. Provision its declared requirements and account for conditional cases until classification is cleaned up. `FROSTSTREAM_TEST_POSTGRES` is an administrator connection string only to a disposable server; existing tests create/drop isolated databases. `FROSTSTREAM_TEST_TYPESENSE` selects the disposable Typesense URL; inspect the existing contract fixture's expected key (`phase4-test-key`) and namespace handling before running it. Never point these variables at an existing installation. A required provider profile must reject missing variables rather than quietly run only SQLite/Lucene. The Lucene measurement test currently requires `FROSTSTREAM_LUCENE_BENCHMARK_REPORT`; idle duration is controlled by `FROSTSTREAM_BENCHMARK_IDLE_SECONDS`.

Do not apply xUnit/VSTest filter assumptions to the repository's Microsoft.Testing.Platform runner. The existing tree filter above is already used by this repository; inspect the installed runner's help/discovery for new category filters. Save the selected test count and names, including all parameterized variants. For a moved/new namespace, update the filter and manifest together.

After implementing and pinning the Playwright harness, these **proposed** commands run from `src/Frontend`. Define and validate fixture inputs `FROSTSTREAM_E2E_LITE_URL` and `FROSTSTREAM_E2E_FULL_URL` in that harness; they must identify owned disposable installations. These variable names are not current production settings.

```sh
pnpm exec playwright install chromium firefox webkit
pnpm exec playwright test --project=core-lite-chromium --retries=0
pnpm exec playwright test --project=core-full-chromium --retries=0
pnpm exec playwright test --project=lite-chromium --retries=0
pnpm exec playwright test --project=full-chromium --retries=0
pnpm exec playwright test --project=core-lite-chromium --grep 'UI-CORE-E-006' --retries=0
```

Add optional/compatibility project commands when their fixture names are implemented. Record fully resolved project names, base URLs with credentials removed and capability profiles per run. Avoid running the entire matrix against one reused mutable installation in parallel. Destructive/crash/restore profiles are exclusive to their owned stack. No GitHub workflow is part of setup or invocation.

For each new case, run the focused variant first and demonstrate that the named assertion would fail for the target defect (for example, a deliberately wrong fixture expectation or a temporary controlled fault). Restore the intended code/expectation immediately. Then run its required provider/edition variants and affected suite. The final acceptance run includes the complete relevant matrix once the implementation is stable. Do not repeatedly run unrelated suites without new changes or a diagnostic reason.

## Completion and evidence

A checklist item is complete only when its stated variants are implemented, discovered and passing with the real required dependencies; negative cases assert that rejected operations have no side effects; cleanup succeeds; and the result is traceable to the tested revision. No item is closed merely because a similarly named old test exists. Coverage percentage can identify gaps but cannot substitute for feature, permission, recovery or browser assertions. Collect backend branch/line coverage if supported by the selected MTP-compatible tooling during implementation; choose coverage tooling then, without silently changing runners.

Proposed local manifest example (create during test implementation):

```json
{
  "runId": "unique-run-id",
  "commit": "tested-revision",
  "workingTreeDiff": "sanitized-diff-artifact-or-clean",
  "id": "UI-CORE-E-006",
  "variant": "Full/chromium/download-local-http",
  "test": "tests/e2e/core/download.spec.ts: named test",
  "command": "record exact invocation",
  "dependencies": { "dotnet": "resolved", "browser": "resolved", "services": "pinned versions" },
  "status": "Passed",
  "assertions": ["one completed run", "original SHA-256 matches", "browser decoded playback"],
  "substitutions": ["owned local origin instead of Internet provider"],
  "artifacts": ["relative sanitized result path"],
  "cleanup": "Passed"
}
```

Track each parameterized provider/protocol/browser/input variant independently; aggregate it under the parent ID only after all required variants pass. Blocked records include the prerequisite, owner and next action. Expected unsupported combinations have an explicit matrix exclusion; missing FFmpeg/container services/hardware are not passing skips. Before user sign-off, require all P0/P1/P2 cases or an explicit user-approved scope exclusion, no unexplained skips, no unresolved flakes, completed restore evidence, and honest reference-machine performance evidence. The budgets in [acceptance.json](src/tooling/performance/acceptance.json) are currently marked provisional; measure against them and obtain a decision before claiming the physical acceptance gate, rather than treating larger-host results as proof.

## Test specifications

Each entry supplies what to test, how to implement it, how to validate it and the inherited requirements recipe. “Starting points” are source/test references found in this checkout, not an assertion that the full scenario already exists or passes. A case that exposes a product defect stays failing until the defect is addressed; never weaken the expected behavior simply to close the checklist.

<a id="suite-core-u"></a>

## Backend Core — unit tests

<a id="core-u-001"></a>

### CORE-U-001 — Deployment mode parsing and obsolete switches

**[Checklist](TEST_CHECKLISTS.md#core-u-001) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Parameterize Full/Lite casing, missing mode, numeric/unknown modes and every forbidden Full single-user key, including false values.

**Validate:** Defaults and valid names resolve once; invalid/obsolete inputs throw before services start.

**Starting points:** [DeploymentOptions.cs](src/Shared/Core/Deployment/DeploymentOptions.cs), [EditionInitializationTests.cs](src/Tests/UnitTests/Deployment/EditionInitializationTests.cs).

<a id="core-u-002"></a>

### CORE-U-002 — Capability contract for both editions

**[Checklist](TEST_CHECKLISTS.md#core-u-002) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Resolve capabilities for both modes with live chat and POT on/off; serialize the public response.

**Validate:** Search stays advertised; access management and every backup flag match the edition matrix; no secrets or internal URLs appear.

**Starting points:** [SystemCapabilities.cs](src/Shared/Core/Deployment/SystemCapabilities.cs), [SystemCapabilitiesController.cs](src/Shared/Modules/Api/Features/System/SystemCapabilitiesController.cs).

<a id="core-u-003"></a>

### CORE-U-003 — Module registration is idempotent

**[Checklist](TEST_CHECKLISTS.md#core-u-003) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Register each shared module twice against controlled services and inspect descriptors/registration markers.

**Validate:** A single implementation and hosted service remain per module; required contracts resolve without duplicate consumers.

**Starting points:** [ModuleCompositionTests.cs](src/Tests/UnitTests/Deployment/ModuleCompositionTests.cs), [ModuleRegistration.cs](src/Shared/Core/Deployment/ModuleRegistration.cs).

<a id="core-u-004"></a>

### CORE-U-004 — Startup gate completion, failure and cancellation

**[Checklist](TEST_CHECKLISTS.md#core-u-004) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Drive initialization and waiting consumers with explicit task barriers; cancel one waiter and fail an initializer.

**Validate:** Consumers cannot run early; failures propagate; one canceled waiter does not release or poison unrelated waiters.

**Starting points:** [EditionInitializationTests.cs](src/Tests/UnitTests/Deployment/EditionInitializationTests.cs), [ApplicationStartupGate.cs](src/Shared/Core/Deployment/ApplicationStartupGate.cs).

<a id="core-u-005"></a>

### CORE-U-005 — Message serialization contracts

**[Checklist](TEST_CHECKLISTS.md#core-u-005) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Round-trip representative commands/events for every subject family using ApplicationJson, including IDs, enums, NodaTime, nullable fields and unknown optional fields.

**Validate:** Values and correlation/run IDs survive; malformed required data fails predictably; fixture JSON detects accidental contract changes.

**Starting points:** [ApplicationJson.cs](src/Shared/Core/Messaging/ApplicationJson.cs), [QueueSerializationProbe.cs](src/Tests/UnitTests/Downloads/QueueSerializationProbe.cs).

<a id="core-u-006"></a>

### CORE-U-006 — Background job routing and request identity

**[Checklist](TEST_CHECKLISTS.md#core-u-006) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Table-test each registered job type, worker route, correlation and background-run request factory.

**Validate:** Each type reaches the intended handler with stable ownership and IDs; unsupported types fail without publishing.

**Starting points:** [ApplicationJobRoutes.cs](src/Shared/Core/Messaging/ApplicationJobRoutes.cs), [BackgroundJobRequestFactory.cs](src/Shared/Core/Messaging/BackgroundJobRequestFactory.cs).

<a id="core-u-007"></a>

### CORE-U-007 — Source URL validation and canonical identity

**[Checklist](TEST_CHECKLISTS.md#core-u-007) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Table-test supported video/playlist/channel forms, tracking parameters, malformed URLs, whitespace and unsupported schemes.

**Validate:** Equivalent sources normalize equally; different providers/media remain distinct; invalid requests never reach acquisition.

**Starting points:** [SourceUrlCanonicalizerTests.cs](src/Tests/UnitTests/Discovery/SourceUrlCanonicalizerTests.cs), [PlaylistUrlDetectorTests.cs](src/Tests/UnitTests/WebAPI/PlaylistUrlDetectorTests.cs), [YtDlpSourceUrlValidator.cs](src/Shared/Modules/Api/Features/Common/YtDlpSourceUrlValidator.cs).

<a id="core-u-008"></a>

### CORE-U-008 — Ignore keyword matching

**[Checklist](TEST_CHECKLISTS.md#core-u-008) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Parameterize literal/regex modes, case, Unicode, blank values, invalid regex and pathological input.

**Validate:** Include/exclude results match the documented option semantics; malformed rules report validation errors and bounded work.

**Starting points:** [IgnoreKeywordMatcherTests.cs](src/Tests/UnitTests/Downloads/IgnoreKeywordMatcherTests.cs), [CreatorDiscoveryIgnoreKeywordTests.cs](src/Tests/UnitTests/Discovery/CreatorDiscoveryIgnoreKeywordTests.cs).

<a id="core-u-009"></a>

### CORE-U-009 — Stable media and creator identifiers

**[Checklist](TEST_CHECKLISTS.md#core-u-009) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise deterministic GUID inputs, provider boundaries, absent channel IDs and canonical creator fallbacks.

**Validate:** Identity stays stable across retries and changes only for a distinct source identity.

**Starting points:** [DeterministicGuidTests.cs](src/Tests/UnitTests/Worker/DeterministicGuidTests.cs), [CreatorIdentityTests.cs](src/Tests/UnitTests/Worker/CreatorIdentityTests.cs).

<a id="core-u-010"></a>

### CORE-U-010 — Metadata mapping and partial provider payloads

**[Checklist](TEST_CHECKLISTS.md#core-u-010) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Map complete, minimal, malformed and Unicode metadata with timestamps, thumbnails, formats, comments and captions.

**Validate:** Required identity is preserved, missing optional values are handled, and ordering/units are explicit.

**Starting points:** [YtDlpMetadataMapperTests.cs](src/Tests/UnitTests/Worker/YtDlpMetadataMapperTests.cs).

<a id="core-u-011"></a>

### CORE-U-011 — Placeholder content detection

**[Checklist](TEST_CHECKLISTS.md#core-u-011) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Feed deleted/private/unavailable and legitimate title/description/provider combinations.

**Validate:** Known placeholders are rejected with actionable reasons while legitimate similarly named media remains accepted.

**Starting points:** [PlaceholderContentDetectorTests.cs](src/Tests/UnitTests/Worker/PlaceholderContentDetectorTests.cs).

<a id="core-u-012"></a>

### CORE-U-012 — Download option merging and config resolution

**[Checklist](TEST_CHECKLISTS.md#core-u-012) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Combine defaults, presets, config sets and request overrides including tri-state false/null values and forbidden options.

**Validate:** Documented precedence wins without mutating inputs or losing explicit false; unknown references produce useful errors.

**Starting points:** [YtDlpOptionsMergerTests.cs](src/Tests/UnitTests/Worker/YtDlpOptionsMergerTests.cs), [DownloadConfigSetResolver.cs](src/Shared/Modules/Api/Features/DownloadConfigSets/DownloadConfigSetResolver.cs).

<a id="core-u-013"></a>

### CORE-U-013 — Worker option limits

**[Checklist](TEST_CHECKLISTS.md#core-u-013) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Parameterize valid, zero, negative and excessive concurrency, timeout and scratch options.

**Validate:** Invalid settings fail before work; supported limits propagate to worker/process configuration.

**Starting points:** [WorkerOptionsTests.cs](src/Tests/UnitTests/Worker/WorkerOptionsTests.cs).

<a id="core-u-014"></a>

### CORE-U-014 — Acquisition failure classification

**[Checklist](TEST_CHECKLISTS.md#core-u-014) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Feed representative yt-dlp exit results, cancellation, timeout, authentication and transient network errors.

**Validate:** Stable failure categories and user-safe messages result; cancellation is not mislabeled as success.

**Starting points:** [YtDlpFailureDetailsTests.cs](src/Tests/UnitTests/Worker/YtDlpFailureDetailsTests.cs).

<a id="core-u-015"></a>

### CORE-U-015 — Local import path validation

**[Checklist](TEST_CHECKLISTS.md#core-u-015) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Test traversal, absolute paths, separators, encoded names, sibling prefixes and invalid manifest paths with pure path rules.

**Validate:** Paths outside the allowed incoming root are rejected; ordinary nested names remain valid.

**Starting points:** [LocalImportPathRulesTests.cs](src/Tests/UnitTests/Imports/LocalImportPathRulesTests.cs).

<a id="core-u-016"></a>

### CORE-U-016 — Storage parameter validation and JSON

**[Checklist](TEST_CHECKLISTS.md#core-u-016) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Parameterize Local, FTP/FTPS/SFTP/NFS/SMB/CIFS, S3-compatible, Azure and GCS settings, auth combinations and boundary ports.

**Validate:** Valid settings round-trip without lost fields; invalid combinations return field-specific errors.

**Starting points:** [StorageRequestValidationTests.cs](src/Tests/UnitTests/Storage/StorageRequestValidationTests.cs), [StorageParametersSerializerTests.cs](src/Tests/UnitTests/Storage/StorageParametersSerializerTests.cs).

<a id="core-u-017"></a>

### CORE-U-017 — Storage secret separation

**[Checklist](TEST_CHECKLISTS.md#core-u-017) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Split, merge, redact and update every credential-bearing storage shape with omitted and explicitly cleared secrets.

**Validate:** Public/persisted configuration has no credentials; retained/replaced/cleared secrets follow the request semantics.

**Starting points:** [StorageSecretSplitterTests.cs](src/Tests/UnitTests/Storage/StorageSecretSplitterTests.cs).

<a id="core-u-018"></a>

### CORE-U-018 — Rooted storage path resolution

**[Checklist](TEST_CHECKLISTS.md#core-u-018) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise object-path normalization, root boundaries, missing keys and platform separators without real remote services.

**Validate:** Storage cannot escape its configured root and encoded object keys are resolved consistently.

**Starting points:** [LocalStoragePathResolverTests.cs](src/Tests/UnitTests/Storage/LocalStoragePathResolverTests.cs), [StorageObjectPath.cs](src/Shared/Core/Storage/StorageObjectPath.cs), [RootedStore.cs](src/Shared/Core/Storage/RootedStore.cs).

<a id="core-u-019"></a>

### CORE-U-019 — Storage cache invalidation

**[Checklist](TEST_CHECKLISTS.md#core-u-019) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use fake stores and a fake change bus to request, update, invalidate and dispose the same key concurrently.

**Validate:** One cached instance is reused; changed/deleted entries are evicted and disposed without poisoning other keys.

**Starting points:** [CachingStoreProviderTests.cs](src/Tests/UnitTests/Storage/CachingStoreProviderTests.cs), [StorageConfigChangedSubscriber.cs](src/Shared/Core/Storage/StorageConfigChangedSubscriber.cs).

<a id="core-u-020"></a>

### CORE-U-020 — Storage provider selection

**[Checklist](TEST_CHECKLISTS.md#core-u-020) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Pass each supported protocol to FluentStoreFactory with fake adapter construction or inspectable options.

**Validate:** Correct adapter/options are selected; unsupported values and missing credentials fail before transfer.

**Starting points:** [FluentStoreFactoryTests.cs](src/Tests/UnitTests/Storage/FluentStoreFactoryTests.cs).

<a id="core-u-021"></a>

### CORE-U-021 — Advanced search query parsing

**[Checklist](TEST_CHECKLISTS.md#core-u-021) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Cover field filters, operators, nested groups, escaping, Unicode, empty terms, malformed syntax and bounds.

**Validate:** Typed queries preserve intended semantics and errors identify bad input without producing provider-specific SQL.

**Starting points:** [AdvancedQueryParserTests.cs](src/Tests/UnitTests/Search/AdvancedQueryParserTests.cs).

<a id="core-u-022"></a>

### CORE-U-022 — Search generation and writer gate rules

**[Checklist](TEST_CHECKLISTS.md#core-u-022) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Interleave writes, rebuild activation, cancellation and stale generations using controlled tasks.

**Validate:** Only the active generation becomes visible; canceled/failed replacement cannot discard the usable index.

**Starting points:** [SearchWriterGate.cs](src/Shared/Core/Search/SearchWriterGate.cs), [SearchActivationBarrierTests.cs](src/Tests/UnitTests/Search/SearchActivationBarrierTests.cs).

<a id="core-u-023"></a>

### CORE-U-023 — Endpoint permission catalog completeness

**[Checklist](TEST_CHECKLISTS.md#core-u-023) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Enumerate actual controller endpoints and permission metadata for shared and Full assemblies.

**Validate:** Every protected action maps to an intentional catalog/policy entry; public and internal-service exceptions are explicit.

**Starting points:** [EndpointCatalogValidatorTests.cs](src/Tests/UnitTests/WebAPI/EndpointCatalogValidatorTests.cs), [EndpointMetadataTests.cs](src/Tests/UnitTests/WebAPI/EndpointMetadataTests.cs).

<a id="core-u-024"></a>

### CORE-U-024 — Safe return URLs

**[Checklist](TEST_CHECKLISTS.md#core-u-024) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Parameterize local paths, query strings, absolute URLs, protocol-relative URLs, backslashes and encoded redirect attacks.

**Validate:** Login/logout redirects remain local or use the configured trusted provider logout contract.

**Starting points:** [LocalReturnPath.cs](src/Shared/Modules/Api/Auth/LocalReturnPath.cs), [AuthControllerTests.cs](src/Tests/UnitTests/WebAPI/Auth/AuthControllerTests.cs).

<a id="core-u-025"></a>

### CORE-U-025 — Shared controller validation and status contracts

**[Checklist](TEST_CHECKLISTS.md#core-u-025) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Table-test all shared controller actions with substitute contracts: valid input, invalid model, missing resource, conflict, timeout and cancellation; record expected statuses per action.

**Validate:** Status/body and command arguments match the API contract; rejected requests perform zero writes/publishes.

**Starting points:** [WebAPI](src/Tests/UnitTests/WebAPI).

<a id="core-u-026"></a>

### CORE-U-026 — Request ownership propagation

**[Checklist](TEST_CHECKLISTS.md#core-u-026) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Invoke notes, playlists, cookies, config sets, notifications, watch state and likes controllers with two principals and hostile owner fields.

**Validate:** Ownership comes from the authenticated principal and cannot be selected through request data.

**Starting points:** [UserNotesControllerTests.cs](src/Tests/UnitTests/WebAPI/UserNotesControllerTests.cs), [UserPlaylistsControllerTests.cs](src/Tests/UnitTests/WebAPI/UserPlaylistsControllerTests.cs), [CookiesControllerTests.cs](src/Tests/UnitTests/WebAPI/CookiesControllerTests.cs).

<a id="core-u-027"></a>

### CORE-U-027 — Download queue and job-state decisions

**[Checklist](TEST_CHECKLISTS.md#core-u-027) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Feed running, stopped, completed, failed and stale runs into stop/start/priority/group/cleanup actions with fake bus results.

**Validate:** Only allowed transitions emit commands and retries get new run identity; stale events cannot rewrite the active run.

**Starting points:** [DownloadQueueControllerTests.cs](src/Tests/UnitTests/WebAPI/DownloadQueueControllerTests.cs), [DownloadQueueMessages.cs](src/Shared/Core/Messaging/DownloadQueueMessages.cs).

<a id="core-u-028"></a>

### CORE-U-028 — Progress aggregation and subscriber lifecycle

**[Checklist](TEST_CHECKLISTS.md#core-u-028) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Drive download, rendition and background hubs with duplicate/out-of-order events and disconnected subscribers.

**Validate:** Snapshots stay coherent, event fan-out is bounded, and unsubscribe/cancel releases resources.

**Starting points:** [DownloadQueueHubTests.cs](src/Tests/UnitTests/WebAPI/DownloadQueueHubTests.cs), [BackgroundJobHubTests.cs](src/Tests/UnitTests/WebAPI/BackgroundJobHubTests.cs), [ProgressForwardGate.cs](src/Shared/Core/Messaging/ProgressForwardGate.cs).

<a id="core-u-029"></a>

### CORE-U-029 — Schedule parsing and job dispatch

**[Checklist](TEST_CHECKLISTS.md#core-u-029) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Table-test cron/timezone/task-type inputs, enabled flags, DST boundaries and fake clock triggers.

**Validate:** Valid schedules map to the right job; invalid schedules fail and a disabled schedule cannot dispatch.

**Starting points:** [SchedulesControllerTests.cs](src/Tests/UnitTests/WebAPI/SchedulesControllerTests.cs), [QuartzScheduleFactory.cs](src/Shared/Modules/Scheduler/Triggers/QuartzScheduleFactory.cs), [TaskTypeRegistry.cs](src/Shared/Modules/Scheduler/Triggers/TaskTypeRegistry.cs).

<a id="core-u-030"></a>

### CORE-U-030 — Notification profile validation

**[Checklist](TEST_CHECKLISTS.md#core-u-030) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Cover every supported provider's required fields, secret placeholders, invalid destinations and event preference combinations.

**Validate:** Clear validation errors prevent sending; public representations never echo credential values.

**Starting points:** [NotificationProfileValidatorTests.cs](src/Tests/UnitTests/Notifications/NotificationProfileValidatorTests.cs).

<a id="core-u-031"></a>

### CORE-U-031 — Media access and rendition selection

**[Checklist](TEST_CHECKLISTS.md#core-u-031) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Supply metadata, versions, MIME/codec combinations and authorization results to access/rendition helpers.

**Validate:** Requested versions and supported formats are selected; unauthorized or missing media is not served.

**Starting points:** [MediaWatchControllerTests.cs](src/Tests/UnitTests/WebAPI/MediaWatchControllerTests.cs), [StreamRenditions.cs](src/Shared/Modules/Api/Features/Media/StreamRenditions.cs), [CastStreamCompatibility.cs](src/Shared/Modules/Api/Features/Media/CastStreamCompatibility.cs).

<a id="core-u-032"></a>

### CORE-U-032 — Podcast and cast token scope

**[Checklist](TEST_CHECKLISTS.md#core-u-032) · Unit · Both · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/validate tokens with fake time: correct and wrong media/channel, tampering, expiry, missing audience and changed signing key.

**Validate:** Only the intended resource and lifetime are accepted; tokens cannot authorize unrelated API mutations.

**Starting points:** [PodcastTokenTests.cs](src/Tests/UnitTests/WebAPI/PodcastTokenTests.cs), [CastTokens.cs](src/Shared/Modules/Api/Features/Media/CastTokens.cs).

<a id="core-u-033"></a>

### CORE-U-033 — Cast URL and session state

**[Checklist](TEST_CHECKLISTS.md#core-u-033) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use fake device protocols to exercise public origin selection, queueing, seek/volume bounds, disconnects and competing commands.

**Validate:** Media URLs are reachable and scoped; invalid commands fail; one device session has consistent state and cleanup.

**Starting points:** [CastMediaUrlBuilderTests.cs](src/Tests/UnitTests/WebAPI/CastMediaUrlBuilderTests.cs), [CastSessionManagerTests.cs](src/Tests/UnitTests/WebAPI/CastSessionManagerTests.cs).

<a id="core-u-034"></a>

### CORE-U-034 — FFmpeg argument and process limits

**[Checklist](TEST_CHECKLISTS.md#core-u-034) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise argument generation for video/audio/thumbnail jobs and CPU/thread/concurrency/cancellation settings.

**Validate:** Input names are passed safely, resource bounds are honored, and errors retain useful diagnostics without leaking secrets.

**Starting points:** [FfmpegProcessLimitsTests.cs](src/Tests/UnitTests/MediaProcessor/FfmpegProcessLimitsTests.cs), [FfmpegRunner.cs](src/Shared/Modules/MediaProcessor/Ffmpeg/FfmpegRunner.cs).

<a id="core-u-035"></a>

### CORE-U-035 — Chat replay parsing

**[Checklist](TEST_CHECKLISTS.md#core-u-035) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Feed recorded supported chat payloads, malformed lines, deleted messages, paid messages, Unicode/emotes and offset boundaries.

**Validate:** Stable typed fragments preserve order/time; bad records do not execute markup or corrupt subsequent records.

**Starting points:** [LiveChatReplayParser.cs](src/Shared/Core/LiveChat/LiveChatReplayParser.cs), [LiveChatFragment.cs](src/Shared/Core/LiveChat/LiveChatFragment.cs).

<a id="core-u-036"></a>

### CORE-U-036 — Backup job request validation

**[Checklist](TEST_CHECKLISTS.md#core-u-036) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Test backup/verify kinds against resolved capabilities, overlapping requests, unavailable clients and cancellations.

**Validate:** Unsupported Lite operations are rejected before work and each accepted job exposes stable progress/failure state.

**Starting points:** [BackupJobServiceTests.cs](src/Tests/UnitTests/Backups/BackupJobServiceTests.cs), [BackupServiceContracts.cs](src/Shared/Core/Backups/BackupServiceContracts.cs).

<a id="core-u-037"></a>

### CORE-U-037 — Media deletion selection and reporting

**[Checklist](TEST_CHECKLISTS.md#core-u-037) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Substitute metadata/storage services and exercise metadata-only, original/rendition deletion, missing blobs and partial failures.

**Validate:** Only selected objects are targeted; failures are reported and authoritative records are not silently orphaned.

**Starting points:** [MetadataAdminControllerDeleteTests.cs](src/Tests/UnitTests/Maintenance/MetadataAdminControllerDeleteTests.cs), [MediaFileDeleteConsumerServiceTests.cs](src/Tests/UnitTests/Maintenance/MediaFileDeleteConsumerServiceTests.cs).

<a id="core-u-038"></a>

### CORE-U-038 — Optional enrichment behavior

**[Checklist](TEST_CHECKLISTS.md#core-u-038) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Stub dislike and POT HTTP clients for success, unavailable service, malformed data, rate limits and cancellation.

**Validate:** Optional failure preserves the base download/metadata; disabled features make zero external requests.

**Starting points:** [ReturnYouTubeDislikeClientTests.cs](src/Tests/UnitTests/Worker/ReturnYouTubeDislikeClientTests.cs), [ReturnYouTubeDislikeMetadataEnricherTests.cs](src/Tests/UnitTests/Worker/ReturnYouTubeDislikeMetadataEnricherTests.cs), [PotOptionsApplier.cs](src/Shared/Modules/Worker/Services/PotOptionsApplier.cs).

<a id="core-u-039"></a>

### CORE-U-039 — Worker discovery and heartbeat expiry

**[Checklist](TEST_CHECKLISTS.md#core-u-039) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use a fake clock and registry bus to register, renew, expire and re-register workers.

**Validate:** Active/expired worker state and routing update predictably; duplicate heartbeats do not multiply workers.

**Starting points:** [WorkerHeartbeatService.cs](src/Shared/Modules/Worker/Services/WorkerHeartbeatService.cs), [WorkerRegistryConsumerService.cs](src/Shared/Modules/DataBridge/Messaging/WorkerRegistryConsumerService.cs).

<a id="core-u-040"></a>

### CORE-U-040 — Query parameter bounds

**[Checklist](TEST_CHECKLISTS.md#core-u-040) · Unit · Both · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Table-test paging, sorting, time windows, random limits and search page sizes across metadata, queues, statistics and user lists.

**Validate:** Limits are bounded and deterministic; invalid values return the agreed validation result without broad queries.

**Starting points:** [MetadataControllerTests.cs](src/Tests/UnitTests/WebAPI/MetadataControllerTests.cs), [StatisticsControllerTests.cs](src/Tests/UnitTests/WebAPI/StatisticsControllerTests.cs), [SearchController.cs](src/Shared/Modules/Api/Features/Search/Controllers/SearchController.cs).


<a id="suite-core-i"></a>

## Backend Core — integration contracts

<a id="core-i-001"></a>

### CORE-I-001 — Fresh schema and repeatable startup

**[Checklist](TEST_CHECKLISTS.md#core-i-001) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Initialize an empty database twice on SQLite and PostgreSQL using production initialization.

**Validate:** Required tables/indexes/history exist once; default records are unique and a second initialization preserves data.

**Starting points:** [SqlitePersistenceFoundationTests.cs](src/Tests/UnitTests/DataBridge/SqlitePersistenceFoundationTests.cs), [PostgresFreshInstallTests.cs](src/Tests/UnitTests/Deployment/PostgresFreshInstallTests.cs).

<a id="core-i-002"></a>

### CORE-I-002 — Supported schema upgrades

**[Checklist](TEST_CHECKLISTS.md#core-i-002) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Construct each supported previous schema version, seed application/workflow records and apply production upgrades on both providers.

**Validate:** Rows, child relations, constraints, indexes and independent workflow history survive; current-version reopen is a no-op.

**Starting points:** [PersistenceUpgradeTests.cs](src/Tests/UnitTests/DataBridge/PersistenceUpgradeTests.cs).

<a id="core-i-003"></a>

### CORE-I-003 — Failed, canceled and concurrent schema upgrades

**[Checklist](TEST_CHECKLISTS.md#core-i-003) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Inject DDL/data failure and cancellation, tamper/gap history and start two initializers against one isolated database.

**Validate:** Rollback leaves a recoverable database, tampered history fails closed, and concurrent initialization agrees on one version.

**Starting points:** [PersistenceUpgradeTests.cs](src/Tests/UnitTests/DataBridge/PersistenceUpgradeTests.cs), [PostgresFreshInstallTests.cs](src/Tests/UnitTests/Deployment/PostgresFreshInstallTests.cs).

<a id="core-i-004"></a>

### CORE-I-004 — Metadata and account repository contracts

**[Checklist](TEST_CHECKLISTS.md#core-i-004) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run the same CRUD/upsert/version/taxonomy/list contract with both real providers and fresh connections.

**Validate:** Returned metadata, timestamps, nulls, Unicode and stable ordering agree across providers; unique/foreign-key constraints apply.

**Starting points:** [CoreRepositoryPersistenceTests.cs](src/Tests/UnitTests/DataBridge/CoreRepositoryPersistenceTests.cs), [MetadataReadService.cs](src/Shared/Modules/DataBridge/Metadata/MetadataReadService.cs).

<a id="core-i-005"></a>

### CORE-I-005 — Concurrent writes and transactional rollback

**[Checklist](TEST_CHECKLISTS.md#core-i-005) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Synchronize conflicting writes, multi-step batch failure, cancellation and lock contention on both providers.

**Validate:** No partial aggregate commits or lost acknowledged writes; conflicts are explicit and retry is bounded.

**Starting points:** [BulkContentionPersistenceTests.cs](src/Tests/UnitTests/DataBridge/BulkContentionPersistenceTests.cs), [ApplicationBatches.cs](src/Shared/Persistence/Persistence/ApplicationBatches.cs).

<a id="core-i-006"></a>

### CORE-I-006 — Durable workflow state and fencing

**[Checklist](TEST_CHECKLISTS.md#core-i-006) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Persist workflow effects, messages, leases and run identities, close the store, reopen and deliver duplicates/stale events.

**Validate:** Committed effects are not repeated and stale owners cannot finalize newer runs; incomplete work resumes under the defined policy.

**Starting points:** [WorkflowPersistenceTests.cs](src/Tests/UnitTests/DataBridge/WorkflowPersistenceTests.cs), [DownloadRestartPersistenceTests.cs](src/Tests/UnitTests/DataBridge/DownloadRestartPersistenceTests.cs).

<a id="core-i-007"></a>

### CORE-I-007 — Download submission through finalization

**[Checklist](TEST_CHECKLISTS.md#core-i-007) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Submit through HTTP/production consumers with deterministic acquisition, real storage and database; exercise single and grouped requests.

**Validate:** Job/run/media records, content hash, original blob and terminal event agree; duplicate submission/delivery follows dedup policy.

**Starting points:** [DownloadQueueFlowTests.cs](src/Tests/IntegrationTests/IntegrationTests/DataBridge/DownloadQueueFlowTests.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="core-i-008"></a>

### CORE-I-008 — Stop, restart and explicit retry

**[Checklist](TEST_CHECKLISTS.md#core-i-008) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Stop acquisition at a barrier, restart the host/consumer, deliver a stale completion, then explicitly start a new run.

**Validate:** Stopped work stays stopped, stale completion is fenced, and only the new run can finish.

**Starting points:** [DownloadRestartPersistenceTests.cs](src/Tests/UnitTests/DataBridge/DownloadRestartPersistenceTests.cs), [DownloadJobsRepositoryTests.cs](src/Tests/IntegrationTests/IntegrationTests/DataBridge/DownloadJobsRepositoryTests.cs).

<a id="core-i-009"></a>

### CORE-I-009 — Queue priorities, groups and cleanup

**[Checklist](TEST_CHECKLISTS.md#core-i-009) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create mixed states/priorities and group members; apply priority changes, group stop/start and history cleanup via real consumers.

**Validate:** Correct rows/events change; active work and unrelated groups survive cleanup and ordering is stable.

**Starting points:** [DownloadQueueFlowTests.cs](src/Tests/IntegrationTests/IntegrationTests/DataBridge/DownloadQueueFlowTests.cs), [DownloadJobsRepositoryTests.cs](src/Tests/IntegrationTests/IntegrationTests/DataBridge/DownloadJobsRepositoryTests.cs).

<a id="core-i-010"></a>

### CORE-I-010 — Creator discovery and playlists

**[Checklist](TEST_CHECKLISTS.md#core-i-010) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Persist a monitored creator and a source playlist; ingest repeated scans, excluded media, partial failures and changed metadata.

**Validate:** New media is queued once; ignored items remain explainable; existing membership/order and scan state survive restart.

**Starting points:** [CreatorDiscoveryRepositoryTests.cs](src/Tests/UnitTests/Discovery/CreatorDiscoveryRepositoryTests.cs), [PlaylistsRepositoryTests.cs](src/Tests/UnitTests/DataBridge/PlaylistsRepositoryTests.cs).

<a id="core-i-011"></a>

### CORE-I-011 — Import session lifecycle

**[Checklist](TEST_CHECKLISTS.md#core-i-011) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use real incoming files through source, scan, mapping, metadata edit, inclusion, probe, review and commit.

**Validate:** Session state and per-item metadata persist; only included valid items become finalized playable media.

**Starting points:** [ImportSessionRepositoryTests.cs](src/Tests/UnitTests/DataBridge/ImportSessionRepositoryTests.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="core-i-012"></a>

### CORE-I-012 — Import rejection, retry, cancel and cleanup

**[Checklist](TEST_CHECKLISTS.md#core-i-012) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Include bad media, duplicates, missing files and an interrupted commit; retry failed items, cancel and purge expired sessions.

**Validate:** Completed items are not duplicated; rejected/excluded records remain accurate; active inputs are not deleted by cleanup.

**Starting points:** [ImportSessionRepositoryTests.cs](src/Tests/UnitTests/DataBridge/ImportSessionRepositoryTests.cs), [LocalImportItemV2Flows.cs](src/Shared/Modules/DataBridge/Flows/LocalImportItemV2Flows.cs).

<a id="core-i-013"></a>

### CORE-I-013 — Import filesystem containment

**[Checklist](TEST_CHECKLISTS.md#core-i-013) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create real symlinks, nested directories and traversal names under a disposable incoming root; swap a link during scan/probe where supported.

**Validate:** No content outside the owned root is read or committed; rejected paths yield safe errors.

**Starting points:** [LocalImportScanConsumerService.cs](src/Shared/Modules/Worker/Services/LocalImportScanConsumerService.cs), [LocalImportPathRulesTests.cs](src/Tests/UnitTests/Imports/LocalImportPathRulesTests.cs).

<a id="core-i-014"></a>

### CORE-I-014 — Storage CRUD and credentials

**[Checklist](TEST_CHECKLISTS.md#core-i-014) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/update/delete real storage configuration through API/consumers with an edition secret store and a disposable local target.

**Validate:** DB/public payloads are redacted, credential changes reach the provider, conflicts are explicit and deletion removes owned secrets.

**Starting points:** [StorageLifecycleTests.cs](src/Tests/IntegrationTests/IntegrationTests/Storage/StorageLifecycleTests.cs), [StorageSecretIsolationTests.cs](src/Tests/IntegrationTests/IntegrationTests/Storage/StorageSecretIsolationTests.cs).

<a id="core-i-015"></a>

### CORE-I-015 — Storage invalidation and failure recovery

**[Checklist](TEST_CHECKLISTS.md#core-i-015) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Prime API/Worker stores, change a storage key, remove it and interrupt the transport/provider.

**Validate:** All relevant caches converge, failed requests return bounded errors, and recovery uses the new configuration.

**Starting points:** [StorageConfigChangedSubscriberTests.cs](src/Tests/IntegrationTests/IntegrationTests/Storage/StorageConfigChangedSubscriberTests.cs), [StorageTransportFailureTests.cs](src/Tests/IntegrationTests/IntegrationTests/Storage/StorageTransportFailureTests.cs).

<a id="core-i-016"></a>

### CORE-I-016 — Original media HTTP semantics

**[Checklist](TEST_CHECKLISTS.md#core-i-016) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Serve a known real file through the production HTTP pipeline; request GET, HEAD, byte/suffix/open ranges, invalid ranges and missing files.

**Validate:** Bytes/hash and Content-Type/Length/Range are correct; supported conditional requests obey their contract; invalid ranges do not leak bytes.

**Starting points:** [MediaStreamReadServiceTests.cs](src/Tests/UnitTests/DataBridge/MediaStreamReadServiceTests.cs), [WebApiHttpTests.cs](src/Tests/IntegrationTests/IntegrationTests/WebAPI/WebApiHttpTests.cs).

<a id="core-i-017"></a>

### CORE-I-017 — Video rendition pipeline

**[Checklist](TEST_CHECKLISTS.md#core-i-017) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Generate a distinct FFmpeg fixture; run production processing/storage/DB/events and fetch the manifest plus every referenced segment.

**Validate:** Terminal state is complete only after readable outputs exist; FFprobe can decode segments and timestamps/codecs match the selected rendition.

**Starting points:** [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs), [StreamRenditionProcessorService.cs](src/Shared/Modules/MediaProcessor/Video/StreamRenditionProcessorService.cs).

<a id="core-i-018"></a>

### CORE-I-018 — Audio rendition and podcast delivery

**[Checklist](TEST_CHECKLISTS.md#core-i-018) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Encode channel items with FFmpeg and request audio status, RSS, tokenized episode GET/HEAD/range and expired tokens.

**Validate:** Audio decodes, feed XML points at scoped working URLs, encoded flags persist and unauthorized tokens fail.

**Starting points:** [AudioRenditionProcessorService.cs](src/Shared/Modules/MediaProcessor/Audio/AudioRenditionProcessorService.cs), [ChannelAudioController.cs](src/Shared/Modules/Api/Features/Media/Controllers/ChannelAudioController.cs).

<a id="core-i-019"></a>

### CORE-I-019 — Thumbnail and asset generation

**[Checklist](TEST_CHECKLISTS.md#core-i-019) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Refresh creator artwork and generate missing media thumbnails using controlled HTTP images and real FFmpeg/filesystem.

**Validate:** Correct images are stored once, missing/corrupt sources report failure, and safe fallback placeholders remain available.

**Starting points:** [AssetCacheWriterTests.cs](src/Tests/UnitTests/Worker/AssetCacheWriterTests.cs), [MediaThumbnailGenerationService.cs](src/Shared/Modules/MediaProcessor/Thumbnails/MediaThumbnailGenerationService.cs), [MediaThumbnailGenerationService.cs](src/Shared/Modules/DataBridge/MediaStream/MediaThumbnailGenerationService.cs).

<a id="core-i-020"></a>

### CORE-I-020 — Captions, comments and sidecars

**[Checklist](TEST_CHECKLISTS.md#core-i-020) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Ingest deterministic multilingual subtitle/comment/metadata sidecars and fetch through API and search hydration.

**Validate:** Text, timing, encoding and media/version association survive; malformed sidecars do not discard the valid original.

**Starting points:** [DownloadSidecarCollectionTests.cs](src/Tests/UnitTests/Worker/DownloadSidecarCollectionTests.cs), [MediaCaptionReadService.cs](src/Shared/Modules/DataBridge/MediaStream/MediaCaptionReadService.cs).

<a id="core-i-021"></a>

### CORE-I-021 — Search semantic parity

**[Checklist](TEST_CHECKLISTS.md#core-i-021) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed identical media/captions/comments into real Lucene and Typesense; run exact/filter/phrase/paging/no-hit and advanced searches.

**Validate:** Expected result membership and documented sort/filter semantics agree; do not require identical relevance scores across engines.

**Starting points:** [SearchProviderContractTests.cs](src/Tests/UnitTests/Search/SearchProviderContractTests.cs), [SearchCorpusTests.cs](src/Tests/UnitTests/Phase0/SearchCorpusTests.cs).

<a id="core-i-022"></a>

### CORE-I-022 — Incremental indexing and tombstones

**[Checklist](TEST_CHECKLISTS.md#core-i-022) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Commit metadata updates/deletes while interrupting the pending-index worker; restart and drain the backlog.

**Validate:** Search eventually reflects authoritative DB state, removed rows disappear and checkpoints cannot acknowledge lost writes.

**Starting points:** [PendingIndexTests.cs](src/Tests/UnitTests/DataBridge/PendingIndexTests.cs), [PendingIndexWorker.cs](src/Shared/Modules/DataBridge/Search/PendingIndexWorker.cs).

<a id="core-i-023"></a>

### CORE-I-023 — Search rebuild activation and rollback

**[Checklist](TEST_CHECKLISTS.md#core-i-023) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Rebuild from persisted records while reads/writes continue; fail/cancel before activation and retry.

**Validate:** Old search remains usable until atomic activation; replacement includes concurrent changes and status distinguishes ready/rebuilding/degraded.

**Starting points:** [SearchProviderContractTests.cs](src/Tests/UnitTests/Search/SearchProviderContractTests.cs), [SearchActivationBarrierTests.cs](src/Tests/UnitTests/Search/SearchActivationBarrierTests.cs).

<a id="core-i-024"></a>

### CORE-I-024 — Watch state and likes persistence

**[Checklist](TEST_CHECKLISTS.md#core-i-024) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Submit progress, watched/unwatched and like/unlike repeatedly for two users with out-of-order timestamps.

**Validate:** Resume/history and counts are stable, idempotent and user-scoped; deleted media is handled consistently.

**Starting points:** [WatchStateConsumerServiceTests.cs](src/Tests/IntegrationTests/IntegrationTests/DataBridge/WatchStateConsumerServiceTests.cs), [MediaLikesControllerTests.cs](src/Tests/UnitTests/WebAPI/MediaLikesControllerTests.cs).

<a id="core-i-025"></a>

### CORE-I-025 — User playlists and notes ownership

**[Checklist](TEST_CHECKLISTS.md#core-i-025) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/read/update/reorder/delete private playlists and notes for two persisted identities via production consumers.

**Validate:** Cross-user list/read/mutation never reveals or changes another user's records, including privileged Full users.

**Starting points:** [UserPlaylistsRepositoryTests.cs](src/Tests/UnitTests/DataBridge/UserPlaylistsRepositoryTests.cs), [UserNotesRepositoryTests.cs](src/Tests/UnitTests/DataBridge/UserNotesRepositoryTests.cs).

<a id="core-i-026"></a>

### CORE-I-026 — Cookie profiles, option presets and config sets

**[Checklist](TEST_CHECKLISTS.md#core-i-026) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** CRUD these profiles with real persistence/secrets; resolve references into a controlled download and delete referenced configurations.

**Validate:** Ownership and precedence persist across restart; secrets stay private and broken references produce useful failures.

**Starting points:** [CookieProfileFlowTests.cs](src/Tests/IntegrationTests/IntegrationTests/Cookies/CookieProfileFlowTests.cs), [OptionPresetsRepositoryTests.cs](src/Tests/UnitTests/DataBridge/OptionPresetsRepositoryTests.cs), [DownloadConfigSetConsumerService.cs](src/Shared/Modules/DataBridge/Messaging/DownloadConfigSetConsumerService.cs).

<a id="core-i-027"></a>

### CORE-I-027 — Notification preferences and dispatch

**[Checklist](TEST_CHECKLISTS.md#core-i-027) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Persist preference/provider combinations and trigger real job events against a local receiver with controlled responses.

**Validate:** Only selected events are sent, payloads identify the correct run/user and retries do not create uncontrolled duplicate delivery.

**Starting points:** [NotificationPreferencesConsumerServiceTests.cs](src/Tests/UnitTests/DataBridge/NotificationPreferencesConsumerServiceTests.cs), [NotificationDispatcher.cs](src/Shared/Modules/DataBridge/Messaging/NotificationDispatcher.cs).

<a id="core-i-028"></a>

### CORE-I-028 — Persistent schedules and maintenance

**[Checklist](TEST_CHECKLISTS.md#core-i-028) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/change/disable/delete schedules against real Quartz and persistence; restart, exercise due/misfire cases and trigger each registered maintenance job.

**Validate:** Scheduling state is hydrated once; disabled/deleted schedules stay inactive and job effects/progress match the requested operation.

**Starting points:** [ScheduledTasksRepositoryTests.cs](src/Tests/UnitTests/DataBridge/ScheduledTasksRepositoryTests.cs), [BackupSchedulerTests.cs](src/Tests/UnitTests/Scheduler/BackupSchedulerTests.cs).

<a id="core-i-029"></a>

### CORE-I-029 — Job progress streams and disconnects

**[Checklist](TEST_CHECKLISTS.md#core-i-029) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Subscribe over actual HTTP SSE to download/background/rendition streams, create work, disconnect/reconnect and cancel subscribers.

**Validate:** Snapshot plus updates converge to persisted terminal state; no cross-user data leaks or lingering subscriber resources.

**Starting points:** [DownloadQueueHubTests.cs](src/Tests/UnitTests/WebAPI/DownloadQueueHubTests.cs), [BackgroundJobHubTests.cs](src/Tests/UnitTests/WebAPI/BackgroundJobHubTests.cs), [RenditionProgressHub.cs](src/Shared/Modules/Api/Features/Media/RenditionProgressHub.cs).

<a id="core-i-030"></a>

### CORE-I-030 — Media deletion across database, storage and search

**[Checklist](TEST_CHECKLISTS.md#core-i-030) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Delete a seeded item with originals, audio/video renditions, captions, thumbnails and search documents; inject one storage failure.

**Validate:** Successful deletion leaves no public orphan; partial failure is visible/retryable and unrelated media remains intact.

**Starting points:** [MediaDeleteExecutorTests.cs](src/Tests/IntegrationTests/IntegrationTests/Maintenance/MediaDeleteExecutorTests.cs).

<a id="core-i-031"></a>

### CORE-I-031 — Bounded retention

**[Checklist](TEST_CHECKLISTS.md#core-i-031) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed expired and active runs, workflow history, imports, staged objects and index backlog; execute production purgers.

**Validate:** Only eligible records/files are removed; referential integrity and active workflow recovery remain intact.

**Starting points:** [ApplicationRetention.cs](src/Shared/Persistence/Persistence/ApplicationRetention.cs), [WorkflowRetentionQueries.cs](src/Shared/Persistence/Persistence/Workflows/WorkflowRetentionQueries.cs), [ImportSessionPurger.cs](src/Shared/Modules/DataBridge/Data/ImportSessionPurger.cs).

<a id="core-i-032"></a>

### CORE-I-032 — Optional integrations disabled

**[Checklist](TEST_CHECKLISTS.md#core-i-032) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start each edition with live chat/POT disabled and unreachable optional addresses, then download, browse, play and search.

**Validate:** Main workflows succeed with zero optional-service calls; configured capability flags and runtime status are consistent.

**Starting points:** [IntegrationStatus.cs](src/Shared/Core/Deployment/IntegrationStatus.cs), [verify-optional-degradation.py](src/tooling/recovery/verify-optional-degradation.py).

<a id="core-i-033"></a>

### CORE-I-033 — Live chat ingestion and queries

**[Checklist](TEST_CHECKLISTS.md#core-i-033) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Enable disposable ClickHouse, ingest a recorded sidecar twice, query offsets/pages and run backfill.

**Validate:** Messages/emotes remain ordered and deduplicated; progress and bounded page results match the fixture.

**Starting points:** [LiveChatIngestService.cs](src/Shared/Modules/DataBridge/LiveChat/LiveChatIngestService.cs), [LiveChatQueryConsumerService.cs](src/Shared/Modules/DataBridge/LiveChat/LiveChatQueryConsumerService.cs).

<a id="core-i-034"></a>

### CORE-I-034 — Optional integration outage and recovery

**[Checklist](TEST_CHECKLISTS.md#core-i-034) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Interrupt ClickHouse/POT/enrichment services after readiness, then restore them during normal work.

**Validate:** Core media operations continue; optional status/error is honest, retries are bounded and recovery does not require data loss.

**Starting points:** [verify-optional-degradation.py](src/tooling/recovery/verify-optional-degradation.py), [IntegrationStatus.cs](src/Shared/Core/Deployment/IntegrationStatus.cs).

<a id="core-i-035"></a>

### CORE-I-035 — Process execution cleanup

**[Checklist](TEST_CHECKLISTS.md#core-i-035) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run real FFmpeg and a controlled acquisition subprocess that hangs/fails; cancel and terminate owning jobs.

**Validate:** Child processes exit, bounded scratch files are removed and failure/cancellation persists without false completion.

**Starting points:** [FfmpegRunner.cs](src/Shared/Modules/MediaProcessor/Ffmpeg/FfmpegRunner.cs), [MediaToolsUpdateManagerTests.cs](src/Tests/UnitTests/Worker/MediaToolsUpdateManagerTests.cs).

<a id="core-i-036"></a>

### CORE-I-036 — Endpoint contract parity

**[Checklist](TEST_CHECKLISTS.md#core-i-036) · Integration · Both providers / editions · P0. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Enumerate actual Full and Lite shared HTTP routes and run valid/invalid/not-found cases through middleware on each.

**Validate:** Shared routes and payloads agree; intended identity/access-management differences match the capability matrix.

**Starting points:** [LiteBackendAccessTests.cs](src/Tests/UnitTests/WebAPI/Auth/LiteBackendAccessTests.cs), [WebApiHttpTests.cs](src/Tests/IntegrationTests/IntegrationTests/WebAPI/WebApiHttpTests.cs).

<a id="core-i-037"></a>

### CORE-I-037 — Statistics and background history accuracy

**[Checklist](TEST_CHECKLISTS.md#core-i-037) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed known jobs/media/watch events, query each statistic/time grouping before and after cleanup and restart.

**Validate:** Counts, units, time boundaries and paging reflect persisted records without double-counting retries.

**Starting points:** [StatisticsReadService.cs](src/Shared/Modules/DataBridge/Statistics/StatisticsReadService.cs), [StatisticsControllerTests.cs](src/Tests/UnitTests/WebAPI/StatisticsControllerTests.cs).

<a id="core-i-038"></a>

### CORE-I-038 — Storage protocol transfer matrix

**[Checklist](TEST_CHECKLISTS.md#core-i-038) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** For every advertised protocol, use a disposable real server or isolated provider account; write/read/range/list/delete a checksum fixture and force an outage.

**Validate:** Exact bytes and cleanup succeed for Local, FTP, FTPS, SFTP, NFS, SMB/CIFS, S3/MinIO, Azure and GCS; unprovisioned providers remain blocked.

**Starting points:** [FluentStoreFactoryTests.cs](src/Tests/UnitTests/Storage/FluentStoreFactoryTests.cs), [Smb1FlushFileBuffersTests.cs](src/Tests/UnitTests/Storage/Smb1FlushFileBuffersTests.cs), [StorageParameters.cs](src/Shared/Core/Storage/StorageParameters.cs).

<a id="core-i-039"></a>

### CORE-I-039 — Host readiness and orderly shutdown

**[Checklist](TEST_CHECKLISTS.md#core-i-039) · Integration · Both providers / editions · P1. **Requirements:** recipe `CI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start/stop each composition at initialization and active-work barriers; probe readiness and liveness separately.

**Validate:** Readiness cannot precede required handlers/schema; shutdown cancels subscribers and preserves recoverable work within a defined timeout.

**Starting points:** [SubscriptionShutdownTests.cs](src/Tests/UnitTests/Deployment/SubscriptionShutdownTests.cs), [LiteHostStartupTests.cs](src/Tests/UnitTests/Deployment/LiteHostStartupTests.cs), [FullReadinessHealthCheck.cs](src/Full/Hosts/WebAPI/FullReadinessHealthCheck.cs).


<a id="suite-full-u"></a>

## Backend Full — unit tests

<a id="full-u-001"></a>

### FULL-U-001 — Full authentication configuration

**[Checklist](TEST_CHECKLISTS.md#full-u-001) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Build Full security options with missing/invalid authority, audience, client credentials, public origin and media transfer token.

**Validate:** Startup rejects incomplete required configuration and forbidden single-user switches before accepting traffic.

**Starting points:** [WebApiHardeningTests.cs](src/Tests/UnitTests/WebAPI/WebApiHardeningTests.cs), [SecurityRegistration.cs](src/Full/Security/SecurityRegistration.cs).

<a id="full-u-002"></a>

### FULL-U-002 — Access token validation

**[Checklist](TEST_CHECKLISTS.md#full-u-002) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Sign deterministic JWT fixtures for valid, expired, future, wrong issuer/audience/key and malformed tokens; control time/key discovery.

**Validate:** Only valid intended tokens authenticate; identity/group mapping is stable and no invalid token falls through to Admin.

**Starting points:** [AccessTokenValidator.cs](src/Full/Security/Auth/AccessTokenValidator.cs).

<a id="full-u-003"></a>

### FULL-U-003 — Permission evaluation and fail-closed behavior

**[Checklist](TEST_CHECKLISTS.md#full-u-003) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Substitute OpenFGA/directory clients with allow, deny, timeout and invalid responses across all endpoint permission axes.

**Validate:** Explicit grants authorize only their resources; deny/unavailable/unknown policies cannot expand access.

**Starting points:** [OpenFgaAuthorizerTests.cs](src/Tests/UnitTests/WebAPI/Auth/OpenFgaAuthorizerTests.cs), [FrostStreamPermissionHandlerTests.cs](src/Tests/UnitTests/WebAPI/Auth/FrostStreamPermissionHandlerTests.cs).

<a id="full-u-004"></a>

### FULL-U-004 — Access policy precedence and reconciliation decisions

**[Checklist](TEST_CHECKLISTS.md#full-u-004) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Parameterize provider/account/media constraints, explicit deny, memberships, conflicts and removed resources.

**Validate:** Effective decisions obey documented precedence and reconciliation generates only the required tuple changes.

**Starting points:** [AccessPolicyDenyEvaluatorTests.cs](src/Tests/UnitTests/DataBridge/AccessPolicyDenyEvaluatorTests.cs), [AccessPolicyOpenFgaServiceTests.cs](src/Tests/UnitTests/WebAPI/Auth/AccessPolicyOpenFgaServiceTests.cs).

<a id="full-u-005"></a>

### FULL-U-005 — OpenFGA model and tuple generation

**[Checklist](TEST_CHECKLISTS.md#full-u-005) · Unit · Full · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise repeated provisioning, unchanged/changed model content, batch limits and tuple create/delete requests.

**Validate:** Model reuse is deterministic and tuple operations are idempotent, correctly scoped and bounded.

**Starting points:** [OpenFgaProvisionerTests.cs](src/Tests/UnitTests/WebAPI/Auth/OpenFgaProvisionerTests.cs), [OpenFgaTupleWriterTests.cs](src/Tests/UnitTests/WebAPI/Auth/OpenFgaTupleWriterTests.cs).

<a id="full-u-006"></a>

### FULL-U-006 — Bundle administration validation

**[Checklist](TEST_CHECKLISTS.md#full-u-006) · Unit · Full · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Test catalog membership, duplicate bundles, invalid endpoints, assigned bundle deletion and denied administrative requests.

**Validate:** Invalid writes fail before external changes and affected policies reference valid bundles.

**Starting points:** [OpenFgaBundleManagementServiceTests.cs](src/Tests/UnitTests/WebAPI/Auth/OpenFgaBundleManagementServiceTests.cs), [AccessControlControllerTests.cs](src/Tests/UnitTests/WebAPI/AccessControlControllerTests.cs).

<a id="full-u-007"></a>

### FULL-U-007 — CSRF policy

**[Checklist](TEST_CHECKLISTS.md#full-u-007) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise unsafe/safe methods, same/cross origin, cookie/bearer/service-token auth and missing/invalid antiforgery tokens against middleware rules.

**Validate:** Browser cookie mutations require the intended CSRF proof; exemptions are explicit and cannot bypass authentication.

**Starting points:** [CsrfProtectionMiddleware.cs](src/Full/Security/Auth/CsrfProtectionMiddleware.cs), [WebApiHardeningTests.cs](src/Tests/UnitTests/WebAPI/WebApiHardeningTests.cs).

<a id="full-u-008"></a>

### FULL-U-008 — Session refresh and revocation decisions

**[Checklist](TEST_CHECKLISTS.md#full-u-008) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Drive BFF cookie/session refresh with fake time and IdP responses: near expiry, rejected refresh, revoked user and simultaneous requests.

**Validate:** Refresh is bounded and persisted, revoked sessions fail, and errors cannot prolong an invalid session indefinitely.

**Starting points:** [BffSessionRefreshService.cs](src/Full/Security/Auth/BffSessionRefreshService.cs), [BffCookieEvents.cs](src/Full/Security/Auth/BffCookieEvents.cs).

<a id="full-u-009"></a>

### FULL-U-009 — MediaProcessor service credential propagation

**[Checklist](TEST_CHECKLISTS.md#full-u-009) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use a recording HTTP handler for download/upload, missing token, escaped paths, errors and cancellation.

**Validate:** Both requests carry X-FrostStream-Service-Token; missing token fails before I/O and logs omit the token.

**Starting points:** [MediaProcessorStorageClient.cs](src/Shared/Modules/MediaProcessor/Storage/MediaProcessorStorageClient.cs).

<a id="full-u-010"></a>

### FULL-U-010 — NATS topology configuration

**[Checklist](TEST_CHECKLISTS.md#full-u-010) · Unit · Full · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Inspect stream/consumer definitions and subjects for downloads, imports, playlists, auth sessions, artifacts and background work.

**Validate:** Retention, durable identity and ownership match each message contract; restarting provisioning does not change semantics.

**Starting points:** [NatsApplicationTransportTests.cs](src/Tests/UnitTests/Deployment/NatsApplicationTransportTests.cs), [AuthSessionsTopologyTests.cs](src/Tests/UnitTests/WebAPI/Auth/AuthSessionsTopologyTests.cs).

<a id="full-u-011"></a>

### FULL-U-011 — OpenBao path and error mapping

**[Checklist](TEST_CHECKLISTS.md#full-u-011) · Unit · Full · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Stub KV responses for storage/cookie/provider secrets, absent values, version changes, sealed server and denied token.

**Validate:** Paths remain correctly scoped, failures are explicit and credentials are not logged or returned in public models.

**Starting points:** [OpenBaoSecretStore.cs](src/Full/Infrastructure/Secrets/OpenBaoSecretStore.cs), [SecretPaths.cs](src/Shared/Core/Secrets/SecretPaths.cs).

<a id="full-u-012"></a>

### FULL-U-012 — Backup coordination and pgBackRest parsing

**[Checklist](TEST_CHECKLISTS.md#full-u-012) · Unit · Full · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise backup kinds, serialized job execution, progress, failed subprocesses, cancellation and malformed pgBackRest JSON.

**Validate:** Job states and parsed chains/timestamps remain accurate; failed backup never produces a usable-success record.

**Starting points:** [BackupCoordinatorTests.cs](src/Tests/UnitTests/Backups/BackupCoordinatorTests.cs), [BackupJobStoreTests.cs](src/Tests/UnitTests/Backups/BackupJobStoreTests.cs), [PgBackRestInfoParserTests.cs](src/Tests/UnitTests/Backups/PgBackRestInfoParserTests.cs).

<a id="full-u-013"></a>

### FULL-U-013 — Backup and OpenBao artifact pairing

**[Checklist](TEST_CHECKLISTS.md#full-u-013) · Unit · Full · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Validate matching/mismatched backup IDs, hashes, recovery material and missing snapshot/export artifacts.

**Validate:** Only a complete compatible recovery set is accepted; failure cannot silently pair unrelated DB and secret snapshots.

**Starting points:** [OpenBaoPairingTests.cs](src/Tests/UnitTests/Backups/OpenBaoPairingTests.cs).


<a id="suite-full-i"></a>

## Backend Full — integration tests

<a id="full-i-001"></a>

### FULL-I-001 — Production Full host composition

**[Checklist](TEST_CHECKLISTS.md#full-i-001) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start actual WebAPI, DataBridge, Worker, MediaProcessor and Scheduler processes with disposable required services and production registration.

**Validate:** Required hosts become ready and resolve only their assigned responsibilities; clean shutdown leaves recoverable state.

**Starting points:** [Hosts](src/Full/Hosts), [StartServices.cs](src/Full/AppHost/StartServices.cs).

<a id="full-i-002"></a>

### FULL-I-002 — Real OIDC login and token exchange

**[Checklist](TEST_CHECKLISTS.md#full-i-002) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use disposable Authentik and real BFF routes for callback, code exchange, session persistence and safe return path; use an HTTP/browser driver as needed.

**Validate:** Issuer/audience/state/nonce failures are rejected and a valid session survives reload against real NATS session storage.

**Starting points:** [check-authentik.mjs](src/tooling/installation/check-authentik.mjs), [BrowserAuthController.cs](src/Full/Security/Controllers/BrowserAuthController.cs).

<a id="full-i-003"></a>

### FULL-I-003 — Session refresh, logout and revocation across instances

**[Checklist](TEST_CHECKLISTS.md#full-i-003) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create two WebAPI instances using shared session storage; refresh then log out/revoke through one and replay cookies against both.

**Validate:** Shared sessions refresh consistently; revoked/logged-out tickets cannot be reused by the other instance.

**Starting points:** [NatsBffTicketStore.cs](src/Full/Security/Auth/NatsBffTicketStore.cs), [MultiUserHttpAuthTests.cs](src/Tests/IntegrationTests/IntegrationTests/Auth/MultiUserHttpAuthTests.cs).

<a id="full-i-004"></a>

### FULL-I-004 — OpenFGA endpoint and resource authorization

**[Checklist](TEST_CHECKLISTS.md#full-i-004) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run controlled JWT actors against real OpenFGA and persisted policies for admin, allowed user, denied user and anonymous access.

**Validate:** Endpoint permissions and media restrictions both apply; public metadata, media, captions, search and streams do not bypass restrictions.

**Starting points:** [OpenFgaAxis1FlowTests.cs](src/Tests/IntegrationTests/IntegrationTests/Auth/OpenFgaAxis1FlowTests.cs), [MultiUserHttpAuthTests.cs](src/Tests/IntegrationTests/IntegrationTests/Auth/MultiUserHttpAuthTests.cs).

<a id="full-i-005"></a>

### FULL-I-005 — Cross-user personal data isolation

**[Checklist](TEST_CHECKLISTS.md#full-i-005) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Attempt list/read/create/update/delete by alternate IDs for playlists, notes, cookies, presets, config sets, notifications, likes and watch state with two identities.

**Validate:** Neither user reads or mutates the other's private records; include two admins and ordinary users.

**Starting points:** [MultiUserHttpAuthTests.cs](src/Tests/IntegrationTests/IntegrationTests/Auth/MultiUserHttpAuthTests.cs), [CookieProfileFlowTests.cs](src/Tests/IntegrationTests/IntegrationTests/Cookies/CookieProfileFlowTests.cs).

<a id="full-i-006"></a>

### FULL-I-006 — CSRF through the real HTTP pipeline

**[Checklist](TEST_CHECKLISTS.md#full-i-006) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Obtain a real cookie session and submit each unsafe verb with absent, wrong and valid antiforgery proof, including logout.

**Validate:** Invalid proof causes no state change; valid requests succeed; bearer/service exceptions stay within their own authorization rules.

**Starting points:** [WebApiHttpTests.cs](src/Tests/IntegrationTests/IntegrationTests/WebAPI/WebApiHttpTests.cs), [CsrfProtectionMiddleware.cs](src/Full/Security/Auth/CsrfProtectionMiddleware.cs).

<a id="full-i-007"></a>

### FULL-I-007 — Internal media transfer authorization

**[Checklist](TEST_CHECKLISTS.md#full-i-007) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start real Full WebAPI and MediaProcessor client; GET/PUT with matching, missing and wrong service tokens, plus an ordinary browser cookie.

**Validate:** Matching token transfers exact bytes; other credentials are denied; traversal is rejected and the token grants no unrelated API permission.

**Starting points:** [MediaStorageTransferController.cs](src/Shared/Modules/Api/Features/Media/Controllers/MediaStorageTransferController.cs), [MediaProcessorStorageClient.cs](src/Shared/Modules/MediaProcessor/Storage/MediaProcessorStorageClient.cs).

<a id="full-i-008"></a>

### FULL-I-008 — Distributed download, import and HLS pipeline

**[Checklist](TEST_CHECKLISTS.md#full-i-008) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run separate production hosts with real NATS/PostgreSQL/storage and a local HTTP media origin; acquire with production yt-dlp and separately import distinct bytes.

**Validate:** Both inputs finalize, hash/range responses match, and HLS segments decode after authenticated processor transfers; inspect failed state before retries.

**Starting points:** [check-full-media.mjs](src/tooling/installation/check-full-media.mjs), [StartServices.cs](src/Full/AppHost/StartServices.cs).

<a id="full-i-009"></a>

### FULL-I-009 — NATS durable redelivery and restart

**[Checklist](TEST_CHECKLISTS.md#full-i-009) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Crash the consumer after receipt and around effect commit/ack, restart NATS/consumer and redeliver duplicates.

**Validate:** Work is eventually processed with at most one committed business effect; stale execution cannot complete a newer run.

**Starting points:** [NatsApplicationTransportTests.cs](src/Tests/UnitTests/Deployment/NatsApplicationTransportTests.cs), [DownloadQueueFlowTests.cs](src/Tests/IntegrationTests/IntegrationTests/DataBridge/DownloadQueueFlowTests.cs).

<a id="full-i-010"></a>

### FULL-I-010 — NATS request/reply failures and bounds

**[Checklist](TEST_CHECKLISTS.md#full-i-010) · Integration · Full · P1. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Remove handlers, interrupt connectivity and saturate queues with bounded loads; restore the service.

**Validate:** Timeouts/errors surface within configured limits, canceled requests do not leak subscriptions and normal operation recovers.

**Starting points:** [StorageTransportFailureTests.cs](src/Tests/IntegrationTests/IntegrationTests/Storage/StorageTransportFailureTests.cs), [NatsApplicationTransport.cs](src/Full/Infrastructure/Messaging/NatsApplicationTransport.cs).

<a id="full-i-011"></a>

### FULL-I-011 — PostgreSQL outage and recovery

**[Checklist](TEST_CHECKLISTS.md#full-i-011) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Interrupt connections during a transaction/workflow and restart PostgreSQL against the same owned volume.

**Validate:** Uncommitted work rolls back, acknowledged data survives and hosts resume without duplicate finalization.

**Starting points:** [WorkflowPersistenceTests.cs](src/Tests/UnitTests/DataBridge/WorkflowPersistenceTests.cs), [FullPersistenceRegistration.cs](src/Full/Infrastructure/Postgres/FullPersistenceRegistration.cs).

<a id="full-i-012"></a>

### FULL-I-012 — OpenBao persistence, restart and rotation

**[Checklist](TEST_CHECKLISTS.md#full-i-012) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Write each secret type through production clients, restart/unseal an owned OpenBao instance and rotate credentials.

**Validate:** Required secrets remain readable only with authorized credentials; no plaintext appears in PostgreSQL/public API/logs.

**Starting points:** [CookieProfileStackFixture.cs](src/Tests/IntegrationTests/IntegrationTests/Infrastructure/CookieProfileStackFixture.cs), [StorageSecretIsolationTests.cs](src/Tests/IntegrationTests/IntegrationTests/Storage/StorageSecretIsolationTests.cs).

<a id="full-i-013"></a>

### FULL-I-013 — Required security service failure

**[Checklist](TEST_CHECKLISTS.md#full-i-013) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Make OpenFGA, Authentik discovery/refresh and OpenBao unavailable in separate variants during startup and steady state.

**Validate:** Protected operations fail closed without grants or credential leakage; readiness/status and recovery accurately reflect the affected operation.

**Starting points:** [OpenFgaStackFixture.cs](src/Tests/IntegrationTests/IntegrationTests/Infrastructure/OpenFgaStackFixture.cs), [FullSecurityPipeline.cs](src/Full/Security/FullSecurityPipeline.cs).

<a id="full-i-014"></a>

### FULL-I-014 — Access bundle/policy reconciliation

**[Checklist](TEST_CHECKLISTS.md#full-i-014) · Integration · Full · P1. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/change/duplicate/delete bundles and policies via API against real OpenFGA/DB, interrupt tuple updates, then restart reconciliation.

**Validate:** Effective rights eventually match persisted policy; partial failures never silently grant broader access.

**Starting points:** [OpenFgaAxis1FlowTests.cs](src/Tests/IntegrationTests/IntegrationTests/Auth/OpenFgaAxis1FlowTests.cs), [AccessPolicyReconciliationService.cs](src/Full/Security/Auth/AccessPolicyReconciliationService.cs).

<a id="full-i-015"></a>

### FULL-I-015 — Typesense provisioning and rebuild recovery

**[Checklist](TEST_CHECKLISTS.md#full-i-015) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start an empty Typesense, build/query shared corpus, restart it and fail a replacement generation during live updates.

**Validate:** Collections/schema and aliases converge; prior generation remains usable until replacement is ready and tombstones persist.

**Starting points:** [SearchProviderContractTests.cs](src/Tests/UnitTests/Search/SearchProviderContractTests.cs), [Typesense](src/Shared/Integrations/Typesense).

<a id="full-i-016"></a>

### FULL-I-016 — Full backup kinds and verification

**[Checklist](TEST_CHECKLISTS.md#full-i-016) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** On disposable PostgreSQL/pgBackRest/OpenBao, take full, differential and incremental backups after distinct writes; verify each chain.

**Validate:** Manifest/chain/progress accurately identifies recoverable artifacts; corrupt/missing dependencies fail verification.

**Starting points:** [BackupCoordinatorTests.cs](src/Tests/UnitTests/Backups/BackupCoordinatorTests.cs), [verify-full-recovery.py](src/tooling/recovery/verify-full-recovery.py).

<a id="full-i-017"></a>

### FULL-I-017 — Full database and secrets restore

**[Checklist](TEST_CHECKLISTS.md#full-i-017) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Back up media/profile records and secrets, modify them, restore the paired artifacts into a separate disposable installation and start all hosts.

**Validate:** Restored records and usable secrets match the snapshot, original media stays readable and derived search is rebuilt correctly.

**Starting points:** [verify-full-recovery.py](src/tooling/recovery/verify-full-recovery.py), [verify-fresh-vault.py](src/tooling/recovery/verify-fresh-vault.py).

<a id="full-i-018"></a>

### FULL-I-018 — PostgreSQL point-in-time recovery

**[Checklist](TEST_CHECKLISTS.md#full-i-018) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Record two known committed markers around a target UTC instant with WAL archived; restore to that point in an isolated destination.

**Validate:** Earlier marker exists and later marker does not; new timeline and follow-up full backup work and timezone boundaries are explicit.

**Starting points:** [RestoreWizard.razor](src/Full/Hosts/BackupService/Components/Pages/RestoreWizard.razor).

<a id="full-i-019"></a>

### FULL-I-019 — Backup recovery failure containment

**[Checklist](TEST_CHECKLISTS.md#full-i-019) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Attempt missing/corrupt WAL, mismatched OpenBao set, inaccessible repository, live-destination and interrupted restore variants.

**Validate:** Clear failure is reported without declaring success or overwriting unrelated installation data; owned destination can be retried safely.

**Starting points:** [OpenBaoPairingTests.cs](src/Tests/UnitTests/Backups/OpenBaoPairingTests.cs), [BackupCoordinatorTests.cs](src/Tests/UnitTests/Backups/BackupCoordinatorTests.cs).

<a id="full-i-020"></a>

### FULL-I-020 — Concurrent distributed workers

**[Checklist](TEST_CHECKLISTS.md#full-i-020) · Integration · Full · P1. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run two workers and processors against the same queue; deliver duplicates, expire leases and stop one during transfer.

**Validate:** One authoritative result is committed, surviving workers make progress and concurrency/resource bounds hold.

**Starting points:** [DownloadFlowV2Repository.cs](src/Shared/Modules/DataBridge/Data/DownloadFlowV2Repository.cs), [StreamRenditionRepository.cs](src/Shared/Modules/DataBridge/StreamRenditions/StreamRenditionRepository.cs).

<a id="full-i-021"></a>

### FULL-I-021 — Full package fresh boot and restart

**[Checklist](TEST_CHECKLISTS.md#full-i-021) · Integration · Full · P0. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Build/generate an owned Full package and use ordinary Compose startup with fresh volumes, then restart with seeded data.

**Validate:** Initializer dependencies and health checks succeed; media service token wiring, mounts and persistence work without manual startup workarounds.

**Starting points:** [package.py](src/tooling/installation/package.py), [check-package.py](src/tooling/installation/check-package.py), [verify-install.py](src/tooling/installation/verify-install.py).

<a id="full-i-022"></a>

### FULL-I-022 — Full transport/storage origin configuration

**[Checklist](TEST_CHECKLISTS.md#full-i-022) · Integration · Full · P1. **Requirements:** recipe `FI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run behind the packaged proxy and a local HTTPS fixture, exercise forwarded origin, secure cookies, cast/media URLs and configured trusted proxies.

**Validate:** Browser and service URLs resolve correctly, secure settings are honored and untrusted forwarded headers cannot rewrite public links.

**Starting points:** [WebApiHardeningTests.cs](src/Tests/UnitTests/WebAPI/WebApiHardeningTests.cs), [CastMediaUrlBuilderTests.cs](src/Tests/UnitTests/WebAPI/CastMediaUrlBuilderTests.cs), [Caddyfile](src/Frontend/Caddyfile).


<a id="suite-lite-u"></a>

## Backend Lite — unit tests

<a id="lite-u-001"></a>

### LITE-U-001 — Local Admin identity

**[Checklist](TEST_CHECKLISTS.md#lite-u-001) · Unit · Lite · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Authenticate with no credentials and with irrelevant expired/invalid external credentials; inspect fixed subject, display name and claims.

**Validate:** All valid Lite requests resolve the same intended local Admin identity without contacting an identity provider.

**Starting points:** [SingleUserAuthenticationHandler.cs](src/Lite/Infrastructure/Identity/SingleUserAuthenticationHandler.cs), [AllowAllAuthorizerTests.cs](src/Tests/UnitTests/WebAPI/Auth/AllowAllAuthorizerTests.cs).

<a id="lite-u-002"></a>

### LITE-U-002 — Central Lite authorization

**[Checklist](TEST_CHECKLISTS.md#lite-u-002) · Unit · Lite · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise default/fallback/named policies, explicit schemes and a newly introduced policy without permission metadata.

**Validate:** Lite permits intended product access centrally; ownership subject remains the local Admin and validation still applies.

**Starting points:** [LiteBackendAccessTests.cs](src/Tests/UnitTests/WebAPI/Auth/LiteBackendAccessTests.cs), [LiteAuthorization.cs](src/Lite/Infrastructure/Identity/LiteAuthorization.cs).

<a id="lite-u-003"></a>

### LITE-U-003 — Lite configuration selects local adapters

**[Checklist](TEST_CHECKLISTS.md#lite-u-003) · Unit · Lite · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Compose Lite with valid local settings and unreachable Full-service addresses; inspect registrations and deployment options.

**Validate:** SQLite, local secrets/messaging and Lucene are selected; optional settings cannot activate Full auth/search/database adapters.

**Starting points:** [ModuleCompositionTests.cs](src/Tests/UnitTests/Deployment/ModuleCompositionTests.cs), [SearchProviderConfigurationTests.cs](src/Tests/UnitTests/Search/SearchProviderConfigurationTests.cs).

<a id="lite-u-004"></a>

### LITE-U-004 — Local queue capacity and cancellation rules

**[Checklist](TEST_CHECKLISTS.md#lite-u-004) · Unit · Lite · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use controlled in-memory consumers and small capacities; test wait/reject behavior, timeouts, request cancellation and disposal.

**Validate:** Backpressure is bounded and canceled/closed waiters finish without losing already accepted in-memory work.

**Starting points:** [BoundedQueueTests.cs](src/Tests/UnitTests/Deployment/BoundedQueueTests.cs), [LocalApplicationTransportTests.cs](src/Tests/UnitTests/Deployment/LocalApplicationTransportTests.cs).

<a id="lite-u-005"></a>

### LITE-U-005 — SQLite value encoding

**[Checklist](TEST_CHECKLISTS.md#lite-u-005) · Unit · Lite · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Round-trip nullable values, timestamps, booleans, IDs, Unicode, JSON and numeric limits through pure encoding helpers.

**Validate:** Encodings preserve supported values and align with provider contract expectations.

**Starting points:** [SqliteStorageEncoding.cs](src/Shared/Persistence/Persistence/Sqlite/SqliteStorageEncoding.cs).

<a id="lite-u-006"></a>

### LITE-U-006 — Lucene document/query translation

**[Checklist](TEST_CHECKLISTS.md#lite-u-006) · Unit · Lite · P1. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Construct typed media/caption/comment documents and filters with missing fields, Unicode, dates and boundary values.

**Validate:** Index fields preserve identity and semantics; unsafe/invalid query values cannot produce arbitrary query syntax.

**Starting points:** [LuceneDocumentCodec.cs](src/Lite/Infrastructure/Search/LuceneDocumentCodec.cs), [LuceneSearchService.cs](src/Lite/Infrastructure/Search/LuceneSearchService.cs).

<a id="lite-u-007"></a>

### LITE-U-007 — Lite recovery command validation

**[Checklist](TEST_CHECKLISTS.md#lite-u-007) · Unit · Lite · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Parse snapshot/destination/key options and unsupported online/destructive combinations with controlled filesystem abstractions where possible.

**Validate:** Missing or unsupported inputs fail before any recovery mutation and errors identify the corrective step.

**Starting points:** [SqliteRecoveryCommand.cs](src/Lite/Infrastructure/Persistence/Sqlite/SqliteRecoveryCommand.cs).

<a id="lite-u-008"></a>

### LITE-U-008 — Lite backup capability rejection

**[Checklist](TEST_CHECKLISTS.md#lite-u-008) · Unit · Lite · P0. **Requirements:** recipe `U` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Request differential, incremental, deep verification and PITR through backup contracts with fake storage.

**Validate:** Unsupported operations are rejected; supported full snapshot/basic verification remain available.

**Starting points:** [SqliteBackupServiceClientTests.cs](src/Tests/UnitTests/Deployment/SqliteBackupServiceClientTests.cs), [SystemCapabilities.cs](src/Shared/Core/Deployment/SystemCapabilities.cs).


<a id="suite-lite-i"></a>

## Backend Lite — integration tests

<a id="lite-i-001"></a>

### LITE-I-001 — One-host composition and removed-service canary

**[Checklist](TEST_CHECKLISTS.md#lite-i-001) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start the production Lite host with every Full-service URL pointing at a TCP canary; use API, download, import, play and search.

**Validate:** One host serves the complete shared flow and the canary observes zero Full-infrastructure connections.

**Starting points:** [LiteRuntimeFixture.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeFixture.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="lite-i-002"></a>

### LITE-I-002 — Local Admin seeding and restart

**[Checklist](TEST_CHECKLISTS.md#lite-i-002) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start a fresh SQLite installation twice, create owned data and reopen after a process restart.

**Validate:** Exactly one stable local Admin and its data remain; stale external auth settings cannot replace the identity.

**Starting points:** [LocalAdministratorSeederTests.cs](src/Tests/UnitTests/DataBridge/LocalAdministratorSeederTests.cs), [LiteBackendAccessTests.cs](src/Tests/UnitTests/WebAPI/Auth/LiteBackendAccessTests.cs).

<a id="lite-i-003"></a>

### LITE-I-003 — SQLite configuration and contention

**[Checklist](TEST_CHECKLISTS.md#lite-i-003) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Verify connection setup/WAL/foreign-key settings, concurrent writers, busy timeout and interrupted writes with fresh connections.

**Validate:** Constraints are enforced, successful commits survive, failed work rolls back and lock retries are bounded.

**Starting points:** [SqlitePersistenceFoundationTests.cs](src/Tests/UnitTests/DataBridge/SqlitePersistenceFoundationTests.cs), [BulkContentionPersistenceTests.cs](src/Tests/UnitTests/DataBridge/BulkContentionPersistenceTests.cs).

<a id="lite-i-004"></a>

### LITE-I-004 — Durable local inbox/outbox restart

**[Checklist](TEST_CHECKLISTS.md#lite-i-004) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Publish/request through SqliteDurableTransport, stop after enqueue/receipt/effect commit and reopen the same directory.

**Validate:** Accepted durable work reappears; duplicate deliveries do not duplicate effects and request/reply correlation remains correct.

**Starting points:** [SqliteDurableTransportTests.cs](src/Tests/UnitTests/Deployment/SqliteDurableTransportTests.cs).

<a id="lite-i-005"></a>

### LITE-I-005 — Durable lease expiry and stale owner fencing

**[Checklist](TEST_CHECKLISTS.md#lite-i-005) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use two consumer instances with controlled time/lease expiry; redeliver to a new owner and submit the old owner's completion.

**Validate:** New owner makes progress and stale completion/ack cannot discard its work.

**Starting points:** [SqliteDurableTransportTests.cs](src/Tests/UnitTests/Deployment/SqliteDurableTransportTests.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="lite-i-006"></a>

### LITE-I-006 — Abrupt process death during active download

**[Checklist](TEST_CHECKLISTS.md#lite-i-006) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Launch the existing child-process crash host and kill it only after an explicit acquisition/commit barrier; restart from its directory.

**Validate:** Startup reconciliation preserves terminal/stopped state, fences crashed run IDs and permits an explicit successful retry.

**Starting points:** [LiteCrashHost.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteCrashHost.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="lite-i-007"></a>

### LITE-I-007 — Local pub/sub and request/reply lifecycle

**[Checklist](TEST_CHECKLISTS.md#lite-i-007) · Integration · Lite · P1. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Drive multiple subscribers, absent handlers, cancellation, backpressure and shutdown through the production local transport.

**Validate:** Intended subscribers receive events, timeouts are bounded and disposal releases every waiter/subscription.

**Starting points:** [LocalApplicationTransportTests.cs](src/Tests/UnitTests/Deployment/LocalApplicationTransportTests.cs), [SubscriptionShutdownTests.cs](src/Tests/UnitTests/Deployment/SubscriptionShutdownTests.cs).

<a id="lite-i-008"></a>

### LITE-I-008 — Encrypted secrets persistence

**[Checklist](TEST_CHECKLISTS.md#lite-i-008) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Store each supported secret, close/reopen SQLite and inspect raw database/WAL/public responses and file permissions.

**Validate:** Values decrypt after restart, plaintext sentinels are absent from public/storage diagnostics and key material is restricted appropriately.

**Starting points:** [SqliteSecretStoreTests.cs](src/Tests/UnitTests/Deployment/SqliteSecretStoreTests.cs).

<a id="lite-i-009"></a>

### LITE-I-009 — Secret key rotation and missing/corrupt keys

**[Checklist](TEST_CHECKLISTS.md#lite-i-009) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Rotate keys after writing old secrets, reopen, then separately omit/corrupt each needed key in a copied fixture.

**Validate:** Old/new values remain readable with the retained key ring; missing/wrong keys fail explicitly without overwriting ciphertext.

**Starting points:** [SqliteSecretStoreTests.cs](src/Tests/UnitTests/Deployment/SqliteSecretStoreTests.cs), [SqliteRecoveryArtifacts.cs](src/Lite/Infrastructure/Persistence/Sqlite/SqliteRecoveryArtifacts.cs).

<a id="lite-i-010"></a>

### LITE-I-010 — Staged object lifecycle

**[Checklist](TEST_CHECKLISTS.md#lite-i-010) · Integration · Lite · P1. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Persist, read, replace and expire import/artifact objects across restart; inject an interrupted write and concurrent cleanup.

**Validate:** Complete objects retain bytes/hash and ownership; incomplete/expired objects are cleaned without removing active content.

**Starting points:** [SqliteStagedObjectStoreTests.cs](src/Tests/UnitTests/Deployment/SqliteStagedObjectStoreTests.cs).

<a id="lite-i-011"></a>

### LITE-I-011 — Embedded Lucene lifecycle

**[Checklist](TEST_CHECKLISTS.md#lite-i-011) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Build a real index, write/update/delete, reopen it and search multilingual text and large documents.

**Validate:** Committed changes survive reopen, deleted documents disappear and index handles are released on shutdown.

**Starting points:** [LuceneLifecycleTests.cs](src/Tests/UnitTests/Search/LuceneLifecycleTests.cs).

<a id="lite-i-012"></a>

### LITE-I-012 — Missing/corrupt index recovery

**[Checklist](TEST_CHECKLISTS.md#lite-i-012) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Delete/corrupt only an owned derived index with authoritative SQLite populated, then restart the host.

**Validate:** Search rebuilds from authoritative state, exposes truthful readiness and does not delete media or user data.

**Starting points:** [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs), [LuceneIndexState.cs](src/Lite/Infrastructure/Search/LuceneIndexState.cs).

<a id="lite-i-013"></a>

### LITE-I-013 — Snapshot restore invalidates stale search

**[Checklist](TEST_CHECKLISTS.md#lite-i-013) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Take a snapshot, add/delete media and index it, restore the earlier DB into a new installation and start with a stale copied index.

**Validate:** Restored search contains exactly restored authoritative content with no later orphan documents.

**Starting points:** [SearchActivationBarrierTests.cs](src/Tests/UnitTests/Search/SearchActivationBarrierTests.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="lite-i-014"></a>

### LITE-I-014 — Online SQLite snapshot integrity

**[Checklist](TEST_CHECKLISTS.md#lite-i-014) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Take a supported full snapshot during active writes and verify database, secret artifacts and manifest.

**Validate:** Snapshot is internally consistent/reopenable and retained keys can decrypt every included secret.

**Starting points:** [SqliteBackupServiceClientTests.cs](src/Tests/UnitTests/Deployment/SqliteBackupServiceClientTests.cs).

<a id="lite-i-015"></a>

### LITE-I-015 — Offline Lite restore round trip

**[Checklist](TEST_CHECKLISTS.md#lite-i-015) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Stop the fixture, restore a verified snapshot into a new directory, start it and read/play/search seeded media and secrets.

**Validate:** Snapshot data and usable secrets are recovered; later data is absent; original media retention/copy requirements are explicit.

**Starting points:** [SqliteRecoveryCommand.cs](src/Lite/Infrastructure/Persistence/Sqlite/SqliteRecoveryCommand.cs), [verify-lite-recovery.py](src/tooling/recovery/verify-lite-recovery.py).

<a id="lite-i-016"></a>

### LITE-I-016 — Lite recovery failure containment

**[Checklist](TEST_CHECKLISTS.md#lite-i-016) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Restore with wrong keys, corrupt/truncated snapshot, nonempty destination, interrupted staging and unwritable directories.

**Validate:** Source snapshot/current installation stay intact, temporary state is cleaned or diagnosable and no partial restore is reported as complete.

**Starting points:** [SqliteRecoveryArtifacts.cs](src/Lite/Infrastructure/Persistence/Sqlite/SqliteRecoveryArtifacts.cs), [SqliteRecoveryCommand.cs](src/Lite/Infrastructure/Persistence/Sqlite/SqliteRecoveryCommand.cs).

<a id="lite-i-017"></a>

### LITE-I-017 — SQLite maintenance and schedule persistence

**[Checklist](TEST_CHECKLISTS.md#lite-i-017) · Integration · Lite · P1. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run scheduled optimize/checkpoint/retention tasks alongside workload and restart the combined host.

**Validate:** Database remains usable, job state is visible and schedules hydrate once without overlapping duplicate effects.

**Starting points:** [LiteSchedulerTests.cs](src/Tests/UnitTests/Scheduler/LiteSchedulerTests.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="lite-i-018"></a>

### LITE-I-018 — Local processor storage pipeline

**[Checklist](TEST_CHECKLISTS.md#lite-i-018) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run audio/video/thumbnail jobs with real local storage and no internal HTTP media transfer service.

**Validate:** Outputs decode and become playable with local access; no Full-service credential or network storage proxy is required.

**Starting points:** [LocalMediaProcessorStorageClientTests.cs](src/Tests/UnitTests/Storage/LocalMediaProcessorStorageClientTests.cs), [LiteRuntimeTests.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs).

<a id="lite-i-019"></a>

### LITE-I-019 — Lite package fresh boot, visible data and restart

**[Checklist](TEST_CHECKLISTS.md#lite-i-019) · Integration · Lite · P0. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Generate/start an owned Lite package, write a media fixture, inspect declared bind paths, then recreate containers with retained data.

**Validate:** Visible installation data contains the expected DB/keys/media; restart preserves them and no Full required service appears.

**Starting points:** [check-packaged-lite.py](src/tooling/installation/check-packaged-lite.py), [test_packaged_lite.py](src/tooling/installation/test_packaged_lite.py), [package.py](src/tooling/installation/package.py).

<a id="lite-i-020"></a>

### LITE-I-020 — Low disk, denied writes and shutdown

**[Checklist](TEST_CHECKLISTS.md#lite-i-020) · Integration · Lite · P1. **Requirements:** recipe `LI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Impose quota/failing filesystem behavior on owned DB/backup/scratch directories and request graceful termination during work.

**Validate:** Failure is actionable, completed originals remain intact, queues do not spin indefinitely and restart can resume/retry safely.

**Starting points:** [SqliteConnectionFactory.cs](src/Lite/Infrastructure/Persistence/Sqlite/SqliteConnectionFactory.cs), [LiteStartupServices.cs](src/Lite/Backend/LiteStartupServices.cs).


<a id="suite-ui-u"></a>

## Frontend supporting unit tests

<a id="ui-u-001"></a>

### UI-U-001 — Capability and permission helpers

**[Checklist](TEST_CHECKLISTS.md#ui-u-001) · Unit · Both · P0. **Requirements:** recipe `JU` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Extend node tests for every capability combination and switch Full to Lite and back in the same process.

**Validate:** Deployment mode determines edition behavior independently of session mode; no stale module-global state leaks between cases.

**Starting points:** [lite-access.test.mjs](src/Frontend/tests/lite-access.test.mjs), [frontendAccess.ts](src/Frontend/src/lib/frontendAccess.ts).

<a id="ui-u-002"></a>

### UI-U-002 — Layout session and return-path decisions

**[Checklist](TEST_CHECKLISTS.md#ui-u-002) · Unit · Both · P0. **Requirements:** recipe `JU` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Invoke the real layout loader with controlled capability/session responses including 401/403/500 and deep-link queries.

**Validate:** Full 401 redirects to the encoded return path; Lite failures remain errors; unavailable capabilities cannot grant access.

**Starting points:** [lite-access.test.mjs](src/Frontend/tests/lite-access.test.mjs), [+layout.ts](src/Frontend/src/routes/+layout.ts).

<a id="ui-u-003"></a>

### UI-U-003 — API fetch, CSRF refresh and errors

**[Checklist](TEST_CHECKLISTS.md#ui-u-003) · Unit · Both · P0. **Requirements:** recipe `JU` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Drive the real API helper with a fake fetch/browser boundary: every unsafe verb, Request objects, external origins, concurrent mutations and invalid-token retry.

**Validate:** CSRF fetch is deduplicated, retry occurs at most once, external requests get no token, logout resets state and errors preserve useful validation detail.

**Starting points:** [http.ts](src/Frontend/src/lib/api/http.ts), [lite-access.test.mjs](src/Frontend/tests/lite-access.test.mjs).

<a id="ui-u-004"></a>

### UI-U-004 — Frontend job-state reducers

**[Checklist](TEST_CHECKLISTS.md#ui-u-004) · Unit · Both · P1. **Requirements:** recipe `JU` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Feed duplicate/out-of-order progress, reconnect snapshots, terminal failures and new run IDs into stores/reducers.

**Validate:** UI state converges to the current run and terminal failures cannot be overwritten by stale progress.

**Starting points:** [jobState.ts](src/Frontend/src/lib/jobs/jobState.ts), [stores](src/Frontend/src/lib/stores).

<a id="ui-u-005"></a>

### UI-U-005 — Options editor tri-state and serialization

**[Checklist](TEST_CHECKLISTS.md#ui-u-005) · Unit · Both · P1. **Requirements:** recipe `JU` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Table-test preset/config form conversion for unset, true, false, numeric, list, empty and invalid values.

**Validate:** Request payload preserves user intent and option precedence without sending hidden defaults or secret placeholders as values.

**Starting points:** [ytDlpPresetOptions.ts](src/Frontend/src/lib/components/profile/ytDlpPresetOptions.ts), [TriStateSelect.svelte](src/Frontend/src/lib/components/profile/TriStateSelect.svelte).

<a id="ui-u-006"></a>

### UI-U-006 — Chat playback time and rendering data

**[Checklist](TEST_CHECKLISTS.md#ui-u-006) · Unit · Both · P1. **Requirements:** recipe `JU` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Drive chat playback controller with fake time, seek forward/back, pause and malformed fragments.

**Validate:** Visible messages match playback offsets, repeated seeks do not duplicate entries and unsafe text stays data.

**Starting points:** [ChatPlaybackController.svelte.ts](src/Frontend/src/lib/components/chat/ChatPlaybackController.svelte.ts), [ChatFragments.svelte](src/Frontend/src/lib/components/chat/ChatFragments.svelte).


<a id="suite-ui-i"></a>

## Frontend supporting component integration tests

<a id="ui-i-001"></a>

### UI-I-001 — Backup component capability variants

**[Checklist](TEST_CHECKLISTS.md#ui-i-001) · Integration · Both · P1. **Requirements:** recipe `JI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Extend the existing actual Svelte render test across all backup flags, missing optional deepVerification and busy/error states.

**Validate:** Only supported controls/help appear for each variant and unavailable operations cannot be submitted.

**Starting points:** [backup-capabilities.test.mjs](src/Frontend/tests/backup-capabilities.test.mjs), [BackupsSection.svelte](src/Frontend/src/lib/components/admin/BackupsSection.svelte).

<a id="ui-i-002"></a>

### UI-I-002 — Shared form and dialog integration

**[Checklist](TEST_CHECKLISTS.md#ui-i-002) · Integration · Both · P1. **Requirements:** recipe `JI` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Render real Drawer/Modal/Select/Stepper and representative storage/import/profile forms with controlled API adapters.

**Validate:** Required validation, keyboard focus, cancel/confirm, disabled submission and server error rendering work together; API mocks are disclosed.

**Starting points:** [ui](src/Frontend/src/lib/components/ui), [profile](src/Frontend/src/lib/components/profile).


<a id="suite-ui-core-e"></a>

## Frontend Core — E2E against both editions

<a id="ui-core-e-001"></a>

### UI-CORE-E-001 — Application boot and primary navigation

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-001) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Open a clean context against each real edition, load capabilities/session and visit Home, Library, Creators, Download, Jobs, Search, Profile and Admin.

**Validate:** Correct navigation and identity render without page errors; routes load real data and edition-specific links follow capabilities.

**Starting points:** [+layout.svelte](src/Frontend/src/routes/+layout.svelte).

<a id="ui-core-e-002"></a>

### UI-CORE-E-002 — Deep links, reload and history

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-002) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Direct-load seeded watch/channel/profile/admin detail routes with query parameters; reload and use browser back/forward through edits/navigation.

**Validate:** Routing survives the proxy/static fallback, intended query state persists and no blank page or redirect loop occurs.

**Starting points:** [Caddyfile](src/Frontend/Caddyfile), [vite.config.ts](src/Frontend/vite.config.ts).

<a id="ui-core-e-003"></a>

### UI-CORE-E-003 — Empty library and first-use state

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-003) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use an empty owned installation and visit library, creators, jobs, search and personal lists.

**Validate:** Clear empty states and available next actions appear; no fake content, infinite spinners or exception pages.

**Starting points:** [+page.svelte](src/Frontend/src/routes/library/+page.svelte).

<a id="ui-core-e-004"></a>

### UI-CORE-E-004 — Library filtering, sorting and pagination

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-004) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed enough varied media to cross pages; exercise each visible filter/tab/sort, thumbnails and item navigation.

**Validate:** Visible results/order/counts match seed data and deep links; changing filters resets paging appropriately.

**Starting points:** [+page.svelte](src/Frontend/src/routes/library/+page.svelte).

<a id="ui-core-e-005"></a>

### UI-CORE-E-005 — Creator and channel browsing

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-005) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Browse creators and library/creators, open a channel, change its tabs/filters and visit a media item.

**Validate:** Creator identity/artwork/counts and media lists agree with stored data; missing artwork uses the intended fallback.

**Starting points:** [+page.svelte](src/Frontend/src/routes/creators/+page.svelte), [+page.svelte](src/Frontend/src/routes/channel/[accountId]/+page.svelte).

<a id="ui-core-e-006"></a>

### UI-CORE-E-006 — Single video download happy path

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-006) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Submit a controlled local source through the Download page, select storage/config and wait for real acquisition/finalization.

**Validate:** Exactly one completed item appears in Jobs and Library, displays expected metadata and plays actual bytes.

**Starting points:** [+page.svelte](src/Frontend/src/routes/download/+page.svelte).

<a id="ui-core-e-007"></a>

### UI-CORE-E-007 — Download form rejection and recovery

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-007) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Submit blank/malformed URL, missing storage and invalid option combinations; then correct them and submit once.

**Validate:** Actionable validation appears, rejected submissions create no job and corrected values produce one accepted request.

**Starting points:** [+page.svelte](src/Frontend/src/routes/download/+page.svelte).

<a id="ui-core-e-008"></a>

### UI-CORE-E-008 — Playlist/channel download and monitoring

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-008) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Submit a fixture source with multiple entries, configure monitoring/ignore rules and trigger a second scan with a new entry.

**Validate:** Group progress and membership are accurate, ignored entries stay excluded and only new eligible media is queued.

**Starting points:** [+page.svelte](src/Frontend/src/routes/download/+page.svelte), [+page.svelte](src/Frontend/src/routes/channel/[accountId]/+page.svelte).

<a id="ui-core-e-009"></a>

### UI-CORE-E-009 — Job controls, stop and restart

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-009) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run a barrier-controlled slow acquisition; use priority, group controls, stop and start/retry from Jobs.

**Validate:** UI reflects persisted state, stopping cancels real work, and restart creates the intended new run without duplicate media.

**Starting points:** [+page.svelte](src/Frontend/src/routes/jobs/+page.svelte), [JobRow.svelte](src/Frontend/src/lib/components/jobs/JobRow.svelte).

<a id="ui-core-e-010"></a>

### UI-CORE-E-010 — Job failure, history and cleanup

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-010) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Force an origin failure, inspect error/history, recover origin and retry; clean eligible history while active work exists.

**Validate:** Failure detail is actionable, retry can finish and cleanup preserves active jobs and unrelated media.

**Starting points:** [+page.svelte](src/Frontend/src/routes/jobs/+page.svelte).

<a id="ui-core-e-011"></a>

### UI-CORE-E-011 — Background and encoding job views

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-011) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start a real metadata task and audio/video encode; visit background/encoding tabs and filter their progress/history.

**Validate:** Progress reaches correct terminal states without reload and distinguishes failed/canceled/completed operations.

**Starting points:** [+page.svelte](src/Frontend/src/routes/jobs/background/+page.svelte), [+page.svelte](src/Frontend/src/routes/jobs/encoding/+page.svelte).

<a id="ui-core-e-012"></a>

### UI-CORE-E-012 — Original video playback controls

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-012) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Open a known media fixture and use play/pause, seek, volume/mute, speed and fullscreen where supported.

**Validate:** Browser media time advances, seek lands within tolerance and control/keyboard state matches real playback without media errors.

**Starting points:** [VideoJs10Player.svelte](src/Frontend/src/lib/components/players/VideoJs10Player.svelte), [+page.svelte](src/Frontend/src/routes/watch/[mediaGuid]/+page.svelte).

<a id="ui-core-e-013"></a>

### UI-CORE-E-013 — HLS generation and rendition playback

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-013) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Request a supported rendition through the player on an item with no rendition, observe progress and play the generated stream.

**Validate:** Real manifest/segments load, decoded time advances and changing supported quality/source preserves usable playback.

**Starting points:** [VideoJs10Player.svelte](src/Frontend/src/lib/components/players/VideoJs10Player.svelte), [RenditionProgressController.cs](src/Shared/Modules/Api/Features/Media/Controllers/RenditionProgressController.cs).

<a id="ui-core-e-014"></a>

### UI-CORE-E-014 — Audio playback and channel podcast

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-014) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Encode eligible channel media, use the audio player and generate/open its podcast feed/link.

**Validate:** Audio time advances, controls work, feed URLs fetch actual encoded media and copied URLs contain only intended scoped tokens.

**Starting points:** [VideoJs10AudioPlayer.svelte](src/Frontend/src/lib/components/players/VideoJs10AudioPlayer.svelte), [+page.svelte](src/Frontend/src/routes/channel/[accountId]/+page.svelte).

<a id="ui-core-e-015"></a>

### UI-CORE-E-015 — Caption selection and transcript navigation

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-015) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed multilingual timed captions including supported styled subtitles; select/disable captions and click transcript timestamps.

**Validate:** Track text appears at expected times, seek follows timestamps and subtitle loading/rendering survives reload.

**Starting points:** [VideoJs10Player.svelte](src/Frontend/src/lib/components/players/VideoJs10Player.svelte), [google-fonts.ts](src/Frontend/src/lib/jassub/google-fonts.ts).

<a id="ui-core-e-016"></a>

### UI-CORE-E-016 — Watch metadata, versions and comments

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-016) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Open a fixture with multiple versions, technical details, chapters/tags and paged comments; select each relevant panel/version.

**Validate:** Display matches the fixture, timestamps seek correctly and missing optional metadata renders a coherent state.

**Starting points:** [+page.svelte](src/Frontend/src/routes/watch/[mediaGuid]/+page.svelte), [WatchComment.svelte](src/Frontend/src/lib/components/watch/WatchComment.svelte).

<a id="ui-core-e-017"></a>

### UI-CORE-E-017 — Resume position and watched history

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-017) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Watch past a checkpoint, leave/reopen/reload, mark watched/unwatched and inspect library history/in-progress views.

**Validate:** Persisted position resumes within tolerance and watched/history changes remain correct after a fresh context/session.

**Starting points:** [watchState.ts](src/Frontend/src/lib/api/watchState.ts).

<a id="ui-core-e-018"></a>

### UI-CORE-E-018 — Likes and saved playlists from watch

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-018) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Like/unlike and save/remove media in a personal playlist from watch and library surfaces.

**Validate:** Counts/state agree across pages and reload; repeating a click/request does not create duplicate membership.

**Starting points:** [SaveToPlaylistButton.svelte](src/Frontend/src/lib/components/SaveToPlaylistButton.svelte), [mediaLikes.ts](src/Frontend/src/lib/api/mediaLikes.ts).

<a id="ui-core-e-019"></a>

### UI-CORE-E-019 — Personal playlists CRUD and ordering

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-019) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create, rename, populate, reorder, remove items and delete playlists through list/new/detail pages.

**Validate:** Changes persist across reload; canceling deletion preserves data and the playback panel follows saved ordering.

**Starting points:** [PlaylistItemsManager.svelte](src/Frontend/src/lib/components/profile/PlaylistItemsManager.svelte), [playlists](src/Frontend/src/routes/profile/playlists).

<a id="ui-core-e-020"></a>

### UI-CORE-E-020 — Notes on media and creators

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-020) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Add/edit/delete notes through watch/channel panels and the profile notes list; filter and navigate to targets.

**Validate:** The same note appears consistently on both surfaces, persists on reload and renders user text safely.

**Starting points:** [TargetNotePanel.svelte](src/Frontend/src/lib/components/TargetNotePanel.svelte), [+page.svelte](src/Frontend/src/routes/profile/notes/+page.svelte).

<a id="ui-core-e-021"></a>

### UI-CORE-E-021 — Basic search with real index

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-021) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed predictable media/caption/comment matches and search from the UI; page/sort and open a result.

**Validate:** Expected items/snippets/time links appear and no-hit/empty searches show appropriate states on both search engines.

**Starting points:** [+page.svelte](src/Frontend/src/routes/search/+page.svelte).

<a id="ui-core-e-022"></a>

### UI-CORE-E-022 — Advanced search and query validation

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-022) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Build supported field/filter expressions, combine criteria and submit malformed syntax via the advanced page.

**Validate:** Results match the shared corpus contract, URL preserves the query and syntax errors are actionable without discarding input.

**Starting points:** [+page.svelte](src/Frontend/src/routes/search/advanced/+page.svelte).

<a id="ui-core-e-023"></a>

### UI-CORE-E-023 — Search lifecycle and outage UI

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-023) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Trigger a real rebuild and controlled search outage/recovery while browsing indexed media.

**Validate:** Banner distinguishes available/rebuilding/degraded states, reads use the prior generation when supported and recovery clears stale errors.

**Starting points:** [SearchStatusBanner.svelte](src/Frontend/src/lib/components/SearchStatusBanner.svelte).

<a id="ui-core-e-024"></a>

### UI-CORE-E-024 — Local storage administration

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-024) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create a disposable local target through admin storage/new, edit its description/path, validate required fields and delete unused targets.

**Validate:** List/detail/forms reflect persisted config; in-use/conflict errors are clear and cancel does not mutate data.

**Starting points:** [storage](src/Frontend/src/routes/admin/storage).

<a id="ui-core-e-025"></a>

### UI-CORE-E-025 — Network and object storage forms

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-025) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise each displayed protocol with fixture credentials and a real test target selected from the protocol matrix; edit secrets without exposing them.

**Validate:** Protocol-specific fields/validation are correct, persisted settings work and responses/UI do not redisplay stored secrets.

**Starting points:** [storage](src/Frontend/src/routes/admin/storage), [StorageParameters.cs](src/Shared/Core/Storage/StorageParameters.cs).

<a id="ui-core-e-026"></a>

### UI-CORE-E-026 — Import wizard complete flow

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-026) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use source/files/mapping/metadata/review/run steps with two distinct real incoming files; exclude one, edit metadata and commit.

**Validate:** Only included media is imported with edited values, progress reaches completion and the resulting media plays/searches.

**Starting points:** [import](src/Frontend/src/routes/admin/import).

<a id="ui-core-e-027"></a>

### UI-CORE-E-027 — Import resume, invalid files and failed-item retry

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-027) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Reload at every wizard step, select unsupported/corrupt media, interrupt one item, retry failed work and cancel another session.

**Validate:** Saved choices persist, failed items have reasons, completed items are not duplicated and canceled sessions stop progressing.

**Starting points:** [import](src/Frontend/src/routes/admin/import).

<a id="ui-core-e-028"></a>

### UI-CORE-E-028 — Import browse and destructive-action boundaries

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-028) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Attempt unavailable roots, traversal-like names and session cleanup from the UI; cancel and confirm cleanup separately.

**Validate:** UI cannot escape allowed roots; validation is clear and cleanup affects only eligible owned sessions.

**Starting points:** [FolderPickerModal.svelte](src/Frontend/src/lib/components/admin/FolderPickerModal.svelte), [ImportNotice.svelte](src/Frontend/src/lib/components/admin/ImportNotice.svelte).

<a id="ui-core-e-029"></a>

### UI-CORE-E-029 — Schedule administration

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-029) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/edit/disable/delete a short-lived fixture schedule using admin list/detail pages, including invalid cron/timezone values.

**Validate:** Validation blocks bad input, enabled schedule produces one intended visible job and disabled/deleted schedules stop firing.

**Starting points:** [schedules](src/Frontend/src/routes/admin/schedules).

<a id="ui-core-e-030"></a>

### UI-CORE-E-030 — Metadata administration and deletion

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-030) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Trigger supported rebuild/refresh/cleanup jobs, inspect progress, and cancel/confirm deletion of a seeded media fixture.

**Validate:** Correct jobs start; deletion removes the selected data from library/search while unrelated items remain playable.

**Starting points:** [+page.svelte](src/Frontend/src/routes/admin/metadata/+page.svelte).

<a id="ui-core-e-031"></a>

### UI-CORE-E-031 — Worker administration

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-031) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Visit workers with active, busy and disconnected fixture workers; trigger supported management actions.

**Validate:** Availability/progress/last-seen information updates and unavailable workers are represented without hanging actions.

**Starting points:** [+page.svelte](src/Frontend/src/routes/admin/workers/+page.svelte).

<a id="ui-core-e-032"></a>

### UI-CORE-E-032 — Statistics display

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-032) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed known activity across time boundaries and visit admin stats with each date/grouping control.

**Validate:** Counts/chart labels/units agree with seed data and empty/loading/error states remain understandable.

**Starting points:** [+page.svelte](src/Frontend/src/routes/admin/stats/+page.svelte).

<a id="ui-core-e-033"></a>

### UI-CORE-E-033 — Shared backup creation and basic verification

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-033) · E2E · Full + Lite · P0. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start a full backup from the shared admin backup section, reload during progress, inspect history and verify its artifact.

**Validate:** One job persists across reload; list/progress/result match backend state and verification failure cannot display success.

**Starting points:** [BackupsSection.svelte](src/Frontend/src/lib/components/admin/BackupsSection.svelte).

<a id="ui-core-e-034"></a>

### UI-CORE-E-034 — Cookie profile management

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-034) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/edit/delete a fixture cookie profile through list/new/detail routes, test invalid input and use it in a controlled download.

**Validate:** Secret content is accepted safely, never redisplayed unexpectedly, and correct profile selection reaches acquisition.

**Starting points:** [cookie-management](src/Frontend/src/routes/profile/cookie-management).

<a id="ui-core-e-035"></a>

### UI-CORE-E-035 — Option preset management

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-035) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/edit/delete presets with scalar/list/tri-state options and invalid values; select a preset in a real download.

**Validate:** Form round-trip preserves values, validation identifies fields and the actual request uses the intended options.

**Starting points:** [option-presets](src/Frontend/src/routes/profile/option-presets).

<a id="ui-core-e-036"></a>

### UI-CORE-E-036 — Download config set management

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-036) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Combine storage, cookie and preset references in a config set; edit/delete it and test missing referenced items.

**Validate:** Saved sets reload correctly, download defaults resolve predictably and broken references show useful errors.

**Starting points:** [config-sets](src/Frontend/src/routes/profile/config-sets).

<a id="ui-core-e-037"></a>

### UI-CORE-E-037 — Notifications and provider management

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-037) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Toggle event preferences; create/edit/test/delete a provider targeting a local receiver and rotate its fixture secret.

**Validate:** Selected events arrive at the receiver, disabled events do not, errors are visible and secret values remain masked.

**Starting points:** [notifications](src/Frontend/src/routes/profile/notifications), [notification-providers](src/Frontend/src/routes/profile/notification-providers).

<a id="ui-core-e-038"></a>

### UI-CORE-E-038 — Appearance, theme and custom CSS

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-038) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Switch themes, edit/reset custom CSS, reload and open a second browser context without saved preferences.

**Validate:** User settings persist at their intended scope, reset restores defaults and ordinary navigation/dialogs remain usable.

**Starting points:** [appearance](src/Frontend/src/routes/profile/appearance).

<a id="ui-core-e-039"></a>

### UI-CORE-E-039 — Error pages and recovery

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-039) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Visit missing resource IDs, stop a dependency through the fixture, trigger failed mutations, then recover and retry through UI.

**Validate:** Distinct missing/forbidden/unavailable messages appear without lost form input, false success, redirect loops or permanent spinners.

**Starting points:** [http.ts](src/Frontend/src/lib/api/http.ts).

<a id="ui-core-e-040"></a>

### UI-CORE-E-040 — SSE reconnect and navigation cleanup

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-040) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Disconnect the browser briefly while real jobs advance, reconnect, switch tabs/routes repeatedly and reopen progress views.

**Validate:** UI converges to authoritative state, avoids duplicate rows/listeners and releases streams after the last subscriber leaves.

**Starting points:** [eventStream.ts](src/Frontend/src/lib/sse/eventStream.ts), [stores](src/Frontend/src/lib/stores).

<a id="ui-core-e-041"></a>

### UI-CORE-E-041 — Keyboard and accessibility journeys

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-041) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Complete navigation, download, player controls, modal confirmation and import steps with keyboard; inspect names/labels/focus and scan representative pages.

**Validate:** Focus order/return, form errors and controls are accessible; no unresolved serious/critical automated findings, with manual checks recorded.

**Starting points:** [ui](src/Frontend/src/lib/components/ui), [RangeSlider.svelte](src/Frontend/src/lib/components/RangeSlider.svelte).

<a id="ui-core-e-042"></a>

### UI-CORE-E-042 — Responsive layouts and touch

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-042) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Repeat navigation, library/search, watch, forms and import on defined narrow phone, tablet and desktop viewport projects.

**Validate:** Primary controls remain reachable, dialogs fit, no unintended horizontal overflow and touch interactions work.

**Starting points:** [+layout.svelte](src/Frontend/src/routes/+layout.svelte).

<a id="ui-core-e-043"></a>

### UI-CORE-E-043 — Untrusted content rendering

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-043) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed titles, comments, notes, filenames, chat fragments and error messages containing HTML/script-like sentinels and hostile links.

**Validate:** Content renders as safe text/allowed markup and no sentinel script executes or unsafe navigation is triggered.

**Starting points:** [WatchComment.svelte](src/Frontend/src/lib/components/watch/WatchComment.svelte), [TargetNotePanel.svelte](src/Frontend/src/lib/components/TargetNotePanel.svelte), [ChatFragments.svelte](src/Frontend/src/lib/components/chat/ChatFragments.svelte).

<a id="ui-core-e-044"></a>

### UI-CORE-E-044 — Rapid interaction and mutation recovery

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-044) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Double-click submit/save/delete, navigate away during writes and return; simulate connection loss after server commit via the owned fault proxy.

**Validate:** No unintended duplicate resources, stale success or lost saved edits; UI reconciles ambiguous outcomes against persisted state.

**Starting points:** [http.ts](src/Frontend/src/lib/api/http.ts), [ConfirmDeleteModal.svelte](src/Frontend/src/lib/components/admin/ConfirmDeleteModal.svelte).

<a id="ui-core-e-045"></a>

### UI-CORE-E-045 — Localized data and time boundaries

**[Checklist](TEST_CHECKLISTS.md#ui-core-e-045) · E2E · Full + Lite · P1. **Requirements:** recipe `EC` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed long Unicode/RTL titles, emoji, large values and dates spanning DST; use two browser timezones.

**Validate:** Text remains legible, timestamps/counts are correctly formatted and schedule/media durations retain their intended meaning.

**Starting points:** [+page.svelte](src/Frontend/src/routes/channel/[accountId]/+page.svelte), [StatisticsChart.svelte](src/Frontend/src/lib/components/admin/StatisticsChart.svelte).


<a id="suite-ui-full-e"></a>

## Frontend Full — E2E

<a id="ui-full-e-001"></a>

### UI-FULL-E-001 — Real Authentik sign-in and return location

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-001) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Begin unauthenticated on a protected deep link with query parameters and complete actual Authentik login.

**Validate:** Browser returns to the intended local route and shows the correct user; auth redirects do not lose query state.

**Starting points:** [check-authentik.mjs](src/tooling/installation/check-authentik.mjs), [BrowserAuthController.cs](src/Full/Security/Controllers/BrowserAuthController.cs).

<a id="ui-full-e-002"></a>

### UI-FULL-E-002 — Sign-out and revoked session

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-002) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Sign out through the UI, revisit a protected route and replay the old session; separately revoke an active fixture user/session.

**Validate:** Protected data is unavailable after revocation/logout and the UI initiates a fresh legitimate sign-in.

**Starting points:** [check-authentik.mjs](src/tooling/installation/check-authentik.mjs), [http.ts](src/Frontend/src/lib/api/http.ts).

<a id="ui-full-e-003"></a>

### UI-FULL-E-003 — Expiry and refresh during use

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-003) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use short-lived disposable IdP sessions; allow refresh while browsing and expire/revoke refresh credentials during an edit.

**Validate:** Valid refresh preserves work; failed refresh leads to one login navigation without a loop or silent mutation success.

**Starting points:** [BffSessionRefreshService.cs](src/Full/Security/Auth/BffSessionRefreshService.cs), [+layout.ts](src/Frontend/src/routes/+layout.ts).

<a id="ui-full-e-004"></a>

### UI-FULL-E-004 — Permission-aware navigation and direct links

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-004) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use admin, allowed reader and denied user contexts; visit hidden admin/access/media links directly and attempt their API mutations.

**Validate:** Navigation matches effective permissions and backend enforcement prevents bypass through URL or request editing.

**Starting points:** [access-control](src/Frontend/src/routes/admin/access-control).

<a id="ui-full-e-005"></a>

### UI-FULL-E-005 — Private data across two browser users

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-005) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create playlists, notes and profile configuration with two contexts; swap captured resource URLs and mutate by foreign IDs.

**Validate:** List/detail/mutation isolation holds even for two admins; each user retains their own data after reload.

**Starting points:** [MultiUserHttpAuthTests.cs](src/Tests/IntegrationTests/IntegrationTests/Auth/MultiUserHttpAuthTests.cs), [check-authentik.mjs](src/tooling/installation/check-authentik.mjs).

<a id="ui-full-e-006"></a>

### UI-FULL-E-006 — Access bundles CRUD

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-006) · E2E · Full · P1. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use access-control list, bundles/new and bundle edit to select endpoints, save, revise and delete fixture bundles.

**Validate:** Catalog selections persist, duplicate/invalid input is clear and assigned-bundle restrictions are honored.

**Starting points:** [BundleManagementSection.svelte](src/Frontend/src/lib/components/admin/BundleManagementSection.svelte), [bundles](src/Frontend/src/routes/admin/access-control/bundles).

<a id="ui-full-e-007"></a>

### UI-FULL-E-007 — Access policies and effective permissions

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-007) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Create/edit/duplicate/delete policies using the current consolidated UI and assign fixture users/groups/resources; open effective permission views.

**Validate:** Persisted rights take effect for the intended users/resources and explicit deny prevents access despite other grants.

**Starting points:** [+page.svelte](src/Frontend/src/routes/admin/access-control/+page.svelte).

<a id="ui-full-e-008"></a>

### UI-FULL-E-008 — Legacy access-management redirects

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-008) · E2E · Full · P1. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Direct-load access-management, bundle-management, media-access and policy new/edit legacy routes with existing fixture IDs.

**Validate:** Routes reach the intended consolidated access UI preserving supported context; no dead links or permission bypass.

**Starting points:** [+page.ts](src/Frontend/src/routes/admin/access-management/+page.ts), [+page.ts](src/Frontend/src/routes/admin/media-access/+page.ts).

<a id="ui-full-e-009"></a>

### UI-FULL-E-009 — Browser CSRF token recovery

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-009) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** With real cookie auth, rotate/invalidate antiforgery state before a UI save and attempt one cross-origin unsafe request from a local hostile-origin fixture.

**Validate:** UI refreshes proof and saves at most once; hostile request produces no mutation; ordinary forbidden errors do not loop.

**Starting points:** [http.ts](src/Frontend/src/lib/api/http.ts), [CsrfProtectionMiddleware.cs](src/Full/Security/Auth/CsrfProtectionMiddleware.cs).

<a id="ui-full-e-010"></a>

### UI-FULL-E-010 — Full backup options

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-010) · E2E · Full · P1. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Open shared backups with Full capabilities and execute differential/incremental/basic/deep verification variants against a disposable backup chain.

**Validate:** Supported options and PostgreSQL guidance appear; history/results reflect the requested real operation.

**Starting points:** [BackupsSection.svelte](src/Frontend/src/lib/components/admin/BackupsSection.svelte).

<a id="ui-full-e-011"></a>

### UI-FULL-E-011 — Restore console full backup

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-011) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Open the separate BackupService restore console, authenticate, select a real fixture backup and restore into an isolated prepared destination.

**Validate:** Wizard checks prerequisites, survives reload during work, reports accurate outcome and restored application records/secrets work afterward.

**Starting points:** [RestoreWizard.razor](src/Full/Hosts/BackupService/Components/Pages/RestoreWizard.razor).

<a id="ui-full-e-012"></a>

### UI-FULL-E-012 — Restore console PITR

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-012) · E2E · Full · P0. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** In the separate console, choose a known UTC recovery point between two committed fixture markers and complete recovery.

**Validate:** UI confirmation displays the exact point, only expected records survive and a new-timeline backup succeeds.

**Starting points:** [RestoreWizard.razor](src/Full/Hosts/BackupService/Components/Pages/RestoreWizard.razor).

<a id="ui-full-e-013"></a>

### UI-FULL-E-013 — Full security dependency outage UI

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-013) · E2E · Full · P1. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Interrupt OpenFGA or Authentik while a real user browses/searches and attempts an administrative mutation; restore the service.

**Validate:** UI gives an actionable error/login outcome and never exposes protected data or reports a denied action as successful.

**Starting points:** [http.ts](src/Frontend/src/lib/api/http.ts), [FullSecurityPipeline.cs](src/Full/Security/FullSecurityPipeline.cs).

<a id="ui-full-e-014"></a>

### UI-FULL-E-014 — HTTPS session and cross-origin media behavior

**[Checklist](TEST_CHECKLISTS.md#ui-full-e-014) · E2E · Full · P1. **Requirements:** recipe `EF` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run the built frontend through the supported proxy with local trusted HTTPS and real secure-cookie configuration; play and cast scoped media URLs.

**Validate:** Cookies and API credentials use the intended origin, streams load without mixed-content/CORS failures and foreign origins receive no session secret.

**Starting points:** [Caddyfile](src/Frontend/Caddyfile), [WebApiHardeningTests.cs](src/Tests/UnitTests/WebAPI/WebApiHardeningTests.cs).


<a id="suite-ui-lite-e"></a>

## Frontend Lite — E2E

<a id="ui-lite-e-001"></a>

### UI-LITE-E-001 — Immediate local Admin session

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-001) · E2E · Lite · P0. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Open a fresh browser with no cookies and deep-link to profile, library and admin in Lite.

**Validate:** Admin identity and shared product pages load without external authentication or a login prompt.

**Starting points:** [frontendAccess.ts](src/Frontend/src/lib/frontendAccess.ts), [+layout.ts](src/Frontend/src/routes/+layout.ts).

<a id="ui-lite-e-002"></a>

### UI-LITE-E-002 — Access-management controls unavailable

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-002) · E2E · Lite · P0. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Inspect main/admin navigation and directly visit every canonical/legacy access-management route.

**Validate:** Full-only controls are absent, direct routes redirect to the intended Lite admin page and no access-management request is issued.

**Starting points:** [+layout.ts](src/Frontend/src/routes/admin/access-control/+layout.ts), [lite-access.test.mjs](src/Frontend/tests/lite-access.test.mjs).

<a id="ui-lite-e-003"></a>

### UI-LITE-E-003 — Shared admin features stay available

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-003) · E2E · Lite · P0. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** As the local Admin, use storage, imports, metadata, schedules, workers, statistics and backups from their direct routes.

**Validate:** Shared features remain usable despite access management being disabled; no Full permissions prompt blocks them.

**Starting points:** [admin](src/Frontend/src/routes/admin).

<a id="ui-lite-e-004"></a>

### UI-LITE-E-004 — Lite backup options and help

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-004) · E2E · Lite · P0. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Open backups and create/verify a SQLite snapshot; inspect every backup type, restore control and help message.

**Validate:** Full snapshot/basic verification are available; differential/incremental/deep/PITR and Full restore-console links are absent; offline recovery guidance is accurate.

**Starting points:** [BackupsSection.svelte](src/Frontend/src/lib/components/admin/BackupsSection.svelte), [backup-capabilities.test.mjs](src/Frontend/tests/backup-capabilities.test.mjs).

<a id="ui-lite-e-005"></a>

### UI-LITE-E-005 — Lite failure does not launch external login

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-005) · E2E · Lite · P0. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Cause a real owned Lite host/session startup failure and recover it while the browser loads or saves.

**Validate:** Error is visible, browser makes no Authentik/login navigation and recovery restores the local session.

**Starting points:** [lite-access.test.mjs](src/Frontend/tests/lite-access.test.mjs), [http.ts](src/Frontend/src/lib/api/http.ts).

<a id="ui-lite-e-006"></a>

### UI-LITE-E-006 — Lite restart preserves user workflow

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-006) · E2E · Lite · P0. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Through the UI create a note/playlist/config, watch media, restart the real Lite process and reopen those pages.

**Validate:** Local identity, saved data, resume position and searchable/playable media remain consistent.

**Starting points:** [LiteRuntimeFixture.cs](src/Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeFixture.cs), [profile](src/Frontend/src/routes/profile).

<a id="ui-lite-e-007"></a>

### UI-LITE-E-007 — Browser after offline restore

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-007) · E2E · Lite · P0. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed UI-visible media/user state, take a snapshot, add later state, perform the separate offline restore into a new fixture and point the frontend at it.

**Validate:** UI shows exactly the restored state, obsolete index entries disappear and restored media/credentials still work.

**Starting points:** [SqliteRecoveryCommand.cs](src/Lite/Infrastructure/Persistence/Sqlite/SqliteRecoveryCommand.cs), [verify-lite-recovery.py](src/tooling/recovery/verify-lite-recovery.py).

<a id="ui-lite-e-008"></a>

### UI-LITE-E-008 — Lucene recovery visible in the browser

**[Checklist](TEST_CHECKLISTS.md#ui-lite-e-008) · E2E · Lite · P1. **Requirements:** recipe `EL` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Remove/corrupt only the disposable search directory, restart Lite and search while the replacement index activates.

**Validate:** Startup/rebuild UI is honest and eventual results match persisted media without requiring Typesense or relogin.

**Starting points:** [SearchStatusBanner.svelte](src/Frontend/src/lib/components/SearchStatusBanner.svelte), [LuceneLifecycleTests.cs](src/Tests/UnitTests/Search/LuceneLifecycleTests.cs).


<a id="suite-ui-opt-e"></a>

## Frontend optional integrations and device E2E

<a id="ui-opt-e-001"></a>

### UI-OPT-E-001 — Live chat disabled

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-001) · E2E · Both; explicit feature/device matrix · P1. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Start each edition with LiveChat:Enabled=false and open media with a recorded chat sidecar.

**Validate:** Unavailable chat actions are hidden/disabled according to capability and no ClickHouse query is required for playback.

**Starting points:** [ChatReplayPanel.svelte](src/Frontend/src/lib/components/chat/ChatReplayPanel.svelte), [SystemCapabilities.cs](src/Shared/Core/Deployment/SystemCapabilities.cs).

<a id="ui-opt-e-002"></a>

### UI-OPT-E-002 — Live chat replay enabled

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-002) · E2E · Both; explicit feature/device matrix · P1. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Enable real ClickHouse, ingest a deterministic chat fixture and play/pause/seek the corresponding media while using replay controls.

**Validate:** Messages/emotes follow playback offsets, virtualized history behaves correctly and seeks do not duplicate messages.

**Starting points:** [ChatReplayPanel.svelte](src/Frontend/src/lib/components/chat/ChatReplayPanel.svelte), [ChatPlaybackController.svelte.ts](src/Frontend/src/lib/components/chat/ChatPlaybackController.svelte.ts).

<a id="ui-opt-e-003"></a>

### UI-OPT-E-003 — Live chat outage and recovery

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-003) · E2E · Both; explicit feature/device matrix · P1. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Stop the owned ClickHouse service during replay, then restore it and reload/retry via the supported UI path.

**Validate:** Chat failure is clear while video stays usable; recovered messages are ordered and the UI exits its error/loading state.

**Starting points:** [LiveChatController.cs](src/Shared/Modules/Api/Features/Media/Controllers/LiveChatController.cs), [ChatReplayPanel.svelte](src/Frontend/src/lib/components/chat/ChatReplayPanel.svelte).

<a id="ui-opt-e-004"></a>

### UI-OPT-E-004 — POT enabled and unavailable

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-004) · E2E · Both; explicit feature/device matrix · P1. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run downloads with POT off, with a controlled functioning provider and with its service unavailable in each edition.

**Validate:** Capability/status and acquisition outcomes match configuration; optional failure does not strand UI progress or expose provider secrets.

**Starting points:** [PotProviderOptions.cs](src/Shared/Modules/Worker/Services/PotProviderOptions.cs), [PotShimService.cs](src/Shared/Modules/Worker/Services/PotShimService.cs).

<a id="ui-opt-e-005"></a>

### UI-OPT-E-005 — Server-managed casting to a receiver

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-005) · E2E · Both; explicit feature/device matrix · P2. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use real FCast-compatible hardware or a protocol-faithful receiver process; discover/select it in ServerCastMenu and play/pause/seek/stop.

**Validate:** Receiver confirms actual decoded playback and commands; session UI converges after disconnect/reconnect and tokens remain scoped.

**Starting points:** [ServerCastMenu.svelte](src/Frontend/src/lib/components/players/ServerCastMenu.svelte), [FCastCastProtocol.cs](src/Shared/Modules/Api/Features/Media/Casting/FCastCastProtocol.cs).

<a id="ui-opt-e-006"></a>

### UI-OPT-E-006 — Browser casting on supported hardware

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-006) · E2E · Both; explicit feature/device matrix · P2. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** In an actual browser/device combination that exposes the supported casting API, select a receiver and control real playback.

**Validate:** Device plays the selected authorized media and browser state/errors reflect connection loss; mocked API results do not satisfy this case.

**Starting points:** [browserCast.ts](src/Frontend/src/lib/components/players/browserCast.ts), [CastDropdown.svelte](src/Frontend/src/lib/components/players/CastDropdown.svelte).

<a id="ui-opt-e-007"></a>

### UI-OPT-E-007 — No casting support or no receivers

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-007) · E2E · Both; explicit feature/device matrix · P1. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run contexts without supported casting API and a backend fixture with zero reachable devices; open player/cast surfaces.

**Validate:** Playback remains usable, unavailable controls and empty discovery state are clear and no repeating console failures occur.

**Starting points:** [CastButton.svelte](src/Frontend/src/lib/components/players/CastButton.svelte), [ServerCastMenu.svelte](src/Frontend/src/lib/components/players/ServerCastMenu.svelte).

<a id="ui-opt-e-008"></a>

### UI-OPT-E-008 — Optional feature combination smoke

**[Checklist](TEST_CHECKLISTS.md#ui-opt-e-008) · E2E · Both; explicit feature/device matrix · P1. **Requirements:** recipe `EO` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run off/off, chat-only, POT-only and both-enabled profiles in each edition; perform one real download, playback and search journey in every profile.

**Validate:** Resolved capabilities agree with configuration, enabled features function and shared workflows remain healthy in every combination.

**Starting points:** [SystemCapabilities.cs](src/Shared/Core/Deployment/SystemCapabilities.cs), [verify-optional-degradation.py](src/tooling/recovery/verify-optional-degradation.py).


<a id="suite-accept-i"></a>

## System acceptance exercised as integration tests

<a id="accept-i-001"></a>

### ACCEPT-I-001 — Solution and dependency boundaries after relocation

**[Checklist](TEST_CHECKLISTS.md#accept-i-001) · Integration · Both; recorded environment matrix · P0. **Requirements:** recipe `A` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Resolve every solution/project reference and run/update architecture tooling for the src layout, then inspect restored Lite/Full dependency graphs.

**Validate:** All references exist; shared code has no edition implementation/host dependency and Lite carries no forbidden Full SDKs.

**Starting points:** [FrostStream.slnx](FrostStream.slnx), [verify.py](src/tooling/architecture/verify.py), [verify-provider-boundaries.py](src/tooling/architecture/verify-provider-boundaries.py), [verify-final-boundaries.py](src/tooling/architecture/verify-final-boundaries.py).

<a id="accept-i-002"></a>

### ACCEPT-I-002 — Packaging and schema tooling regression

**[Checklist](TEST_CHECKLISTS.md#accept-i-002) · Integration · Both; recorded environment matrix · P1. **Requirements:** recipe `A` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Exercise package/start/path/schema tooling with its focused Python tests and real generated packages after correcting any stale root assumptions.

**Validate:** Commands work from the documented repository root, immutable baselines are checked and production package contexts include required props/SDK files.

**Starting points:** [test_start_packaged.py](src/tooling/installation/test_start_packaged.py), [verify-baselines.py](src/tooling/recovery/verify-baselines.py), [test_verify.py](src/tooling/architecture/test_verify.py).

<a id="accept-i-003"></a>

### ACCEPT-I-003 — Reference corpus indexing and latency

**[Checklist](TEST_CHECKLISTS.md#accept-i-003) · Integration · Both; recorded environment matrix · P2. **Requirements:** recipe `A` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Seed the prescribed media/caption/comment corpus through each real provider and measure full/incremental indexing plus warm and separately characterized cold queries.

**Validate:** Correct results precede timing; publish raw samples and p50/p95/p99 against documented budgets, disclosing cache conditions.

**Starting points:** [acceptance.json](src/tooling/performance/acceptance.json), [LuceneBenchmarkTests.cs](src/Tests/UnitTests/Search/LuceneBenchmarkTests.cs).

<a id="accept-i-004"></a>

### ACCEPT-I-004 — Sustained combined workload

**[Checklist](TEST_CHECKLISTS.md#accept-i-004) · Integration · Both; recorded environment matrix · P2. **Requirements:** recipe `A` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Run the reference concurrent download/transcode/index workload for 60 minutes on each required edition/profile using repeatable local inputs.

**Validate:** No OOM, failed jobs, corrupt media or unbounded queue/index/scratch growth; resource accounting includes child processes and optional services separately.

**Starting points:** [acceptance.json](src/tooling/performance/acceptance.json), [measure-containers.py](src/tooling/performance/measure-containers.py).

<a id="accept-i-005"></a>

### ACCEPT-I-005 — Reference-machine startup and idle resources

**[Checklist](TEST_CHECKLISTS.md#accept-i-005) · Integration · Both; recorded environment matrix · P2. **Requirements:** recipe `A` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Measure fresh start, retained-data restart and 15-minute idle on the specified physical memory class for Lite and Full.

**Validate:** Readiness, CPU/memory and available-memory budgets are reported against the actual hardware; a larger development machine cannot close the reference-machine gate.

**Starting points:** [acceptance.json](src/tooling/performance/acceptance.json), [current-validation.md](src/docs/verification/current-validation.md).

<a id="accept-i-006"></a>

### ACCEPT-I-006 — External provider acquisition compatibility

**[Checklist](TEST_CHECKLISTS.md#accept-i-006) · Integration · Both; recorded environment matrix · P1. **Requirements:** recipe `A` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Use a dedicated explicit external-service profile to acquire allowed small fixtures from each supported provider, including cookie/POT-required cases.

**Validate:** Actual provider acquisition and metadata remain compatible; outages/rate limits are recorded distinctly and this profile is never substituted for deterministic offline coverage.

**Starting points:** [YtDlpMetadataMapperTests.cs](src/Tests/UnitTests/Worker/YtDlpMetadataMapperTests.cs), [YtDlpSharpLib](src/Shared/Integrations/Vendor/YtDlpSharpLib).

<a id="accept-i-007"></a>

### ACCEPT-I-007 — Test isolation, cleanup and meaningful discovery

**[Checklist](TEST_CHECKLISTS.md#accept-i-007) · Integration · Both; recorded environment matrix · P1. **Requirements:** recipe `A` in [fixture recipes](#requirements-and-fixture-recipes), plus the dependencies/data named below.

**Implement:** Execute each implemented suite alone, then a representative mixed run with unique owned resources; inspect discovery, explicit skips, retained artifacts and cleanup failures.

**Validate:** Every planned ID/variant is discovered or blocked with a reason, missing prerequisites cannot pass by early return and no test touches another run or developer data.

**Starting points:** [Tests](src/Tests), [tests](src/Frontend/tests).

## Frontend route coverage map

Every current page route, including redirects, is mapped below. Shared layout loading/navigation/errors are covered by UI-CORE-E-001/002/039/040 across the pages; layout files do not represent separate URLs. For each mapped form/list/detail route, include initial loading, success, empty where applicable, validation/server failure and reload. This map must grow whenever a route is added.

| Current route | Required checklist cases |
| --- | --- |
| `/` | [UI-CORE-E-001](TEST_CHECKLISTS.md#ui-core-e-001), [UI-CORE-E-003](TEST_CHECKLISTS.md#ui-core-e-003) |
| `/admin` | [UI-CORE-E-001](TEST_CHECKLISTS.md#ui-core-e-001), [UI-CORE-E-033](TEST_CHECKLISTS.md#ui-core-e-033), [UI-LITE-E-003](TEST_CHECKLISTS.md#ui-lite-e-003) |
| `/admin/access-control` | [UI-FULL-E-004](TEST_CHECKLISTS.md#ui-full-e-004), [UI-FULL-E-007](TEST_CHECKLISTS.md#ui-full-e-007), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/access-control/bundles/[bundleId]/edit` | [UI-FULL-E-006](TEST_CHECKLISTS.md#ui-full-e-006), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/access-control/bundles/new` | [UI-FULL-E-006](TEST_CHECKLISTS.md#ui-full-e-006), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/access-control/policies/[policyId]/edit` | [UI-FULL-E-007](TEST_CHECKLISTS.md#ui-full-e-007), [UI-FULL-E-008](TEST_CHECKLISTS.md#ui-full-e-008), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/access-control/policies/new` | [UI-FULL-E-007](TEST_CHECKLISTS.md#ui-full-e-007), [UI-FULL-E-008](TEST_CHECKLISTS.md#ui-full-e-008), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/access-management` | [UI-FULL-E-008](TEST_CHECKLISTS.md#ui-full-e-008), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/bundle-management` | [UI-FULL-E-008](TEST_CHECKLISTS.md#ui-full-e-008), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/import` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-027](TEST_CHECKLISTS.md#ui-core-e-027), [UI-CORE-E-028](TEST_CHECKLISTS.md#ui-core-e-028) |
| `/admin/import/[sessionId]` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-027](TEST_CHECKLISTS.md#ui-core-e-027) |
| `/admin/import/[sessionId]/files` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-027](TEST_CHECKLISTS.md#ui-core-e-027), [UI-CORE-E-028](TEST_CHECKLISTS.md#ui-core-e-028) |
| `/admin/import/[sessionId]/mapping` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-027](TEST_CHECKLISTS.md#ui-core-e-027) |
| `/admin/import/[sessionId]/metadata` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-027](TEST_CHECKLISTS.md#ui-core-e-027) |
| `/admin/import/[sessionId]/review` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-027](TEST_CHECKLISTS.md#ui-core-e-027) |
| `/admin/import/[sessionId]/run` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-027](TEST_CHECKLISTS.md#ui-core-e-027) |
| `/admin/import/new/source` | [UI-CORE-E-026](TEST_CHECKLISTS.md#ui-core-e-026), [UI-CORE-E-028](TEST_CHECKLISTS.md#ui-core-e-028) |
| `/admin/media-access` | [UI-FULL-E-008](TEST_CHECKLISTS.md#ui-full-e-008), [UI-LITE-E-002](TEST_CHECKLISTS.md#ui-lite-e-002) |
| `/admin/metadata` | [UI-CORE-E-030](TEST_CHECKLISTS.md#ui-core-e-030), [UI-CORE-E-023](TEST_CHECKLISTS.md#ui-core-e-023) |
| `/admin/schedules` | [UI-CORE-E-029](TEST_CHECKLISTS.md#ui-core-e-029) |
| `/admin/schedules/[key]` | [UI-CORE-E-029](TEST_CHECKLISTS.md#ui-core-e-029) |
| `/admin/stats` | [UI-CORE-E-032](TEST_CHECKLISTS.md#ui-core-e-032) |
| `/admin/storage` | [UI-CORE-E-024](TEST_CHECKLISTS.md#ui-core-e-024), [UI-CORE-E-025](TEST_CHECKLISTS.md#ui-core-e-025) |
| `/admin/storage/[key]` | [UI-CORE-E-024](TEST_CHECKLISTS.md#ui-core-e-024), [UI-CORE-E-025](TEST_CHECKLISTS.md#ui-core-e-025) |
| `/admin/storage/new` | [UI-CORE-E-024](TEST_CHECKLISTS.md#ui-core-e-024), [UI-CORE-E-025](TEST_CHECKLISTS.md#ui-core-e-025) |
| `/admin/workers` | [UI-CORE-E-031](TEST_CHECKLISTS.md#ui-core-e-031) |
| `/channel/[accountId]` | [UI-CORE-E-005](TEST_CHECKLISTS.md#ui-core-e-005), [UI-CORE-E-008](TEST_CHECKLISTS.md#ui-core-e-008), [UI-CORE-E-014](TEST_CHECKLISTS.md#ui-core-e-014), [UI-CORE-E-020](TEST_CHECKLISTS.md#ui-core-e-020) |
| `/creators` | [UI-CORE-E-005](TEST_CHECKLISTS.md#ui-core-e-005), [UI-CORE-E-003](TEST_CHECKLISTS.md#ui-core-e-003) |
| `/download` | [UI-CORE-E-006](TEST_CHECKLISTS.md#ui-core-e-006), [UI-CORE-E-007](TEST_CHECKLISTS.md#ui-core-e-007), [UI-CORE-E-008](TEST_CHECKLISTS.md#ui-core-e-008) |
| `/jobs` | [UI-CORE-E-009](TEST_CHECKLISTS.md#ui-core-e-009), [UI-CORE-E-010](TEST_CHECKLISTS.md#ui-core-e-010), [UI-CORE-E-040](TEST_CHECKLISTS.md#ui-core-e-040) |
| `/jobs/background` | [UI-CORE-E-011](TEST_CHECKLISTS.md#ui-core-e-011), [UI-CORE-E-040](TEST_CHECKLISTS.md#ui-core-e-040) |
| `/jobs/encoding` | [UI-CORE-E-011](TEST_CHECKLISTS.md#ui-core-e-011), [UI-CORE-E-013](TEST_CHECKLISTS.md#ui-core-e-013) |
| `/library` | [UI-CORE-E-003](TEST_CHECKLISTS.md#ui-core-e-003), [UI-CORE-E-004](TEST_CHECKLISTS.md#ui-core-e-004), [UI-CORE-E-017](TEST_CHECKLISTS.md#ui-core-e-017), [UI-CORE-E-018](TEST_CHECKLISTS.md#ui-core-e-018) |
| `/library/creators` | [UI-CORE-E-005](TEST_CHECKLISTS.md#ui-core-e-005) |
| `/profile` | [UI-CORE-E-001](TEST_CHECKLISTS.md#ui-core-e-001), [UI-FULL-E-001](TEST_CHECKLISTS.md#ui-full-e-001), [UI-LITE-E-001](TEST_CHECKLISTS.md#ui-lite-e-001) |
| `/profile/appearance` | [UI-CORE-E-038](TEST_CHECKLISTS.md#ui-core-e-038) |
| `/profile/appearance/custom-css` | [UI-CORE-E-038](TEST_CHECKLISTS.md#ui-core-e-038) |
| `/profile/config-sets` | [UI-CORE-E-036](TEST_CHECKLISTS.md#ui-core-e-036) |
| `/profile/config-sets/[key]` | [UI-CORE-E-036](TEST_CHECKLISTS.md#ui-core-e-036) |
| `/profile/config-sets/new` | [UI-CORE-E-036](TEST_CHECKLISTS.md#ui-core-e-036) |
| `/profile/cookie-management` | [UI-CORE-E-034](TEST_CHECKLISTS.md#ui-core-e-034) |
| `/profile/cookie-management/[key]` | [UI-CORE-E-034](TEST_CHECKLISTS.md#ui-core-e-034) |
| `/profile/cookie-management/new` | [UI-CORE-E-034](TEST_CHECKLISTS.md#ui-core-e-034) |
| `/profile/notes` | [UI-CORE-E-020](TEST_CHECKLISTS.md#ui-core-e-020) |
| `/profile/notification-providers/[key]` | [UI-CORE-E-037](TEST_CHECKLISTS.md#ui-core-e-037) |
| `/profile/notification-providers/new` | [UI-CORE-E-037](TEST_CHECKLISTS.md#ui-core-e-037) |
| `/profile/notifications` | [UI-CORE-E-037](TEST_CHECKLISTS.md#ui-core-e-037) |
| `/profile/option-presets` | [UI-CORE-E-035](TEST_CHECKLISTS.md#ui-core-e-035) |
| `/profile/option-presets/[key]` | [UI-CORE-E-035](TEST_CHECKLISTS.md#ui-core-e-035) |
| `/profile/option-presets/new` | [UI-CORE-E-035](TEST_CHECKLISTS.md#ui-core-e-035) |
| `/profile/playlists` | [UI-CORE-E-019](TEST_CHECKLISTS.md#ui-core-e-019) |
| `/profile/playlists/[playlistId]` | [UI-CORE-E-019](TEST_CHECKLISTS.md#ui-core-e-019) |
| `/profile/playlists/new` | [UI-CORE-E-019](TEST_CHECKLISTS.md#ui-core-e-019) |
| `/search` | [UI-CORE-E-021](TEST_CHECKLISTS.md#ui-core-e-021), [UI-CORE-E-023](TEST_CHECKLISTS.md#ui-core-e-023) |
| `/search/advanced` | [UI-CORE-E-022](TEST_CHECKLISTS.md#ui-core-e-022) |
| `/watch/[mediaGuid]` | [UI-CORE-E-012](TEST_CHECKLISTS.md#ui-core-e-012), [UI-CORE-E-013](TEST_CHECKLISTS.md#ui-core-e-013), [UI-CORE-E-015](TEST_CHECKLISTS.md#ui-core-e-015), [UI-CORE-E-016](TEST_CHECKLISTS.md#ui-core-e-016), [UI-CORE-E-017](TEST_CHECKLISTS.md#ui-core-e-017), [UI-OPT-E-002](TEST_CHECKLISTS.md#ui-opt-e-002), [UI-OPT-E-005](TEST_CHECKLISTS.md#ui-opt-e-005), [UI-OPT-E-006](TEST_CHECKLISTS.md#ui-opt-e-006) |

**Advertised route missing from this checkout:** `/admin/backups` is linked by Admin navigation but has no page. Track UI-CORE-E-001/033, UI-FULL-E-010 and UI-LITE-E-004 against this intended route; do not count SSR rendering of BackupsSection as browser route coverage. Once the page exists, add it to the table above.

The separate Full BackupService Blazor restore UI is covered by UI-FULL-E-011/012, including its login and recovery prerequisites. It is not a Svelte route.

## Backend HTTP surface coverage map

Each controller below also inherits CORE-U-025 (per-action input/status/error contract) and CORE-I-036 (real middleware/edition parity where applicable). Expand each actual HTTP verb/route into named success, validation, missing/conflict and authorization variants; do not treat one GET as coverage of all mutations. Cross-user cases apply to Full sessions; provider-level repository ownership contracts can use multiple identities on either database without introducing a multi-user Lite product mode. Shared API suites must enumerate the real endpoint catalog so new actions cannot silently escape the map.

| Controller | Feature-specific checklist cases |
| --- | --- |
| [AccessControlController.cs](src/Full/Security/Controllers/AccessControlController.cs) | [FULL-U-004](TEST_CHECKLISTS.md#full-u-004), [FULL-U-006](TEST_CHECKLISTS.md#full-u-006), [FULL-I-004](TEST_CHECKLISTS.md#full-i-004), [FULL-I-014](TEST_CHECKLISTS.md#full-i-014) |
| [AuthController.cs](src/Shared/Modules/Api/Features/Auth/Controllers/AuthController.cs) | [CORE-U-024](TEST_CHECKLISTS.md#core-u-024), [CORE-I-036](TEST_CHECKLISTS.md#core-i-036), [FULL-I-002](TEST_CHECKLISTS.md#full-i-002), [LITE-I-002](TEST_CHECKLISTS.md#lite-i-002) |
| [BackgroundJobsController.cs](src/Shared/Modules/Api/Features/BackgroundJobs/Controllers/BackgroundJobsController.cs) | [CORE-U-028](TEST_CHECKLISTS.md#core-u-028), [CORE-I-029](TEST_CHECKLISTS.md#core-i-029), [CORE-I-037](TEST_CHECKLISTS.md#core-i-037) |
| [BackupsController.cs](src/Shared/Modules/Api/Features/Backups/Controllers/BackupsController.cs) | [CORE-U-036](TEST_CHECKLISTS.md#core-u-036), [FULL-I-016](TEST_CHECKLISTS.md#full-i-016), [LITE-I-014](TEST_CHECKLISTS.md#lite-i-014) |
| [BrowserAuthController.cs](src/Full/Security/Controllers/BrowserAuthController.cs) | [FULL-I-002](TEST_CHECKLISTS.md#full-i-002), [FULL-I-003](TEST_CHECKLISTS.md#full-i-003), [FULL-I-006](TEST_CHECKLISTS.md#full-i-006) |
| [CastController.cs](src/Shared/Modules/Api/Features/Media/Controllers/CastController.cs) | [CORE-U-032](TEST_CHECKLISTS.md#core-u-032), [CORE-U-033](TEST_CHECKLISTS.md#core-u-033), [UI-OPT-E-005](TEST_CHECKLISTS.md#ui-opt-e-005), [UI-OPT-E-006](TEST_CHECKLISTS.md#ui-opt-e-006) |
| [ChannelAudioController.cs](src/Shared/Modules/Api/Features/Media/Controllers/ChannelAudioController.cs) | [CORE-U-032](TEST_CHECKLISTS.md#core-u-032), [CORE-I-018](TEST_CHECKLISTS.md#core-i-018), [FULL-I-004](TEST_CHECKLISTS.md#full-i-004) |
| [CookiesController.cs](src/Shared/Modules/Api/Features/Cookies/Controllers/CookiesController.cs) | [CORE-U-026](TEST_CHECKLISTS.md#core-u-026), [CORE-I-026](TEST_CHECKLISTS.md#core-i-026), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [CreatorMonitorController.cs](src/Shared/Modules/Api/Features/CreatorMonitor/Controllers/CreatorMonitorController.cs) | [CORE-U-008](TEST_CHECKLISTS.md#core-u-008), [CORE-I-010](TEST_CHECKLISTS.md#core-i-010), [UI-CORE-E-008](TEST_CHECKLISTS.md#ui-core-e-008) |
| [DownloadConfigSetsController.cs](src/Shared/Modules/Api/Features/DownloadConfigSets/Controllers/DownloadConfigSetsController.cs) | [CORE-U-012](TEST_CHECKLISTS.md#core-u-012), [CORE-I-026](TEST_CHECKLISTS.md#core-i-026), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [DownloadQueueController.cs](src/Shared/Modules/Api/Features/Downloads/Controllers/DownloadQueueController.cs) | [CORE-U-027](TEST_CHECKLISTS.md#core-u-027), [CORE-I-008](TEST_CHECKLISTS.md#core-i-008), [CORE-I-009](TEST_CHECKLISTS.md#core-i-009), [CORE-I-029](TEST_CHECKLISTS.md#core-i-029) |
| [DownloadsController.cs](src/Shared/Modules/Api/Features/Downloads/Controllers/DownloadsController.cs) | [CORE-U-007](TEST_CHECKLISTS.md#core-u-007), [CORE-U-012](TEST_CHECKLISTS.md#core-u-012), [CORE-I-007](TEST_CHECKLISTS.md#core-i-007) |
| [ImportSessionsController.cs](src/Shared/Modules/Api/Features/Imports/Controllers/ImportSessionsController.cs) | [CORE-I-011](TEST_CHECKLISTS.md#core-i-011), [CORE-I-012](TEST_CHECKLISTS.md#core-i-012), [CORE-I-013](TEST_CHECKLISTS.md#core-i-013) |
| [LiveChatController.cs](src/Shared/Modules/Api/Features/Media/Controllers/LiveChatController.cs) | [CORE-I-033](TEST_CHECKLISTS.md#core-i-033), [CORE-I-034](TEST_CHECKLISTS.md#core-i-034), [UI-OPT-E-001](TEST_CHECKLISTS.md#ui-opt-e-001), [UI-OPT-E-002](TEST_CHECKLISTS.md#ui-opt-e-002) |
| [LocalBrowserAuthController.cs](src/Lite/Infrastructure/Identity/LocalBrowserAuthController.cs) | [LITE-U-001](TEST_CHECKLISTS.md#lite-u-001), [LITE-U-002](TEST_CHECKLISTS.md#lite-u-002), [UI-LITE-E-001](TEST_CHECKLISTS.md#ui-lite-e-001) |
| [MediaLikesController.cs](src/Shared/Modules/Api/Features/Media/Controllers/MediaLikesController.cs) | [CORE-I-024](TEST_CHECKLISTS.md#core-i-024), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [MediaStorageTransferController.cs](src/Shared/Modules/Api/Features/Media/Controllers/MediaStorageTransferController.cs) | [FULL-U-009](TEST_CHECKLISTS.md#full-u-009), [FULL-I-007](TEST_CHECKLISTS.md#full-i-007), [CORE-I-017](TEST_CHECKLISTS.md#core-i-017), [CORE-I-018](TEST_CHECKLISTS.md#core-i-018) |
| [MediaStreamController.cs](src/Shared/Modules/Api/Features/Media/Controllers/MediaStreamController.cs) | [CORE-I-017](TEST_CHECKLISTS.md#core-i-017), [FULL-I-004](TEST_CHECKLISTS.md#full-i-004), [FULL-I-008](TEST_CHECKLISTS.md#full-i-008) |
| [MediaWatchController.cs](src/Shared/Modules/Api/Features/Media/Controllers/MediaWatchController.cs) | [CORE-I-016](TEST_CHECKLISTS.md#core-i-016), [CORE-I-020](TEST_CHECKLISTS.md#core-i-020), [FULL-I-004](TEST_CHECKLISTS.md#full-i-004) |
| [MediaWatchStateController.cs](src/Shared/Modules/Api/Features/Media/Controllers/MediaWatchStateController.cs) | [CORE-I-024](TEST_CHECKLISTS.md#core-i-024), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [MetadataAdminController.cs](src/Shared/Modules/Api/Features/Metadata/Controllers/MetadataAdminController.cs) | [CORE-U-037](TEST_CHECKLISTS.md#core-u-037), [CORE-I-023](TEST_CHECKLISTS.md#core-i-023), [CORE-I-030](TEST_CHECKLISTS.md#core-i-030) |
| [MetadataController.cs](src/Shared/Modules/Api/Features/Metadata/Controllers/MetadataController.cs) | [CORE-U-040](TEST_CHECKLISTS.md#core-u-040), [CORE-I-004](TEST_CHECKLISTS.md#core-i-004), [CORE-I-019](TEST_CHECKLISTS.md#core-i-019), [CORE-I-020](TEST_CHECKLISTS.md#core-i-020) |
| [NotificationsController.cs](src/Shared/Modules/Api/Features/Notifications/Controllers/NotificationsController.cs) | [CORE-U-030](TEST_CHECKLISTS.md#core-u-030), [CORE-I-027](TEST_CHECKLISTS.md#core-i-027), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [OptionPresetsController.cs](src/Shared/Modules/Api/Features/OptionPresets/Controllers/OptionPresetsController.cs) | [CORE-U-012](TEST_CHECKLISTS.md#core-u-012), [CORE-I-026](TEST_CHECKLISTS.md#core-i-026), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [PlaylistsController.cs](src/Shared/Modules/Api/Features/Playlists/Controllers/PlaylistsController.cs) | [CORE-I-010](TEST_CHECKLISTS.md#core-i-010), [UI-CORE-E-008](TEST_CHECKLISTS.md#ui-core-e-008) |
| [RenditionProgressController.cs](src/Shared/Modules/Api/Features/Media/Controllers/RenditionProgressController.cs) | [CORE-U-028](TEST_CHECKLISTS.md#core-u-028), [CORE-I-029](TEST_CHECKLISTS.md#core-i-029) |
| [RenditionQueueController.cs](src/Shared/Modules/Api/Features/Media/Controllers/RenditionQueueController.cs) | [CORE-I-017](TEST_CHECKLISTS.md#core-i-017), [CORE-I-018](TEST_CHECKLISTS.md#core-i-018), [UI-CORE-E-011](TEST_CHECKLISTS.md#ui-core-e-011) |
| [SchedulesController.cs](src/Shared/Modules/Api/Features/Schedules/Controllers/SchedulesController.cs) | [CORE-U-029](TEST_CHECKLISTS.md#core-u-029), [CORE-I-028](TEST_CHECKLISTS.md#core-i-028) |
| [SearchController.cs](src/Shared/Modules/Api/Features/Search/Controllers/SearchController.cs) | [CORE-U-021](TEST_CHECKLISTS.md#core-u-021), [CORE-I-021](TEST_CHECKLISTS.md#core-i-021), [CORE-I-023](TEST_CHECKLISTS.md#core-i-023) |
| [StatisticsController.cs](src/Shared/Modules/Api/Features/Statistics/Controllers/StatisticsController.cs) | [CORE-U-040](TEST_CHECKLISTS.md#core-u-040), [CORE-I-037](TEST_CHECKLISTS.md#core-i-037) |
| [StorageController.cs](src/Shared/Modules/Api/Features/Storage/Controllers/StorageController.cs) | [CORE-U-016](TEST_CHECKLISTS.md#core-u-016), [CORE-U-017](TEST_CHECKLISTS.md#core-u-017), [CORE-I-014](TEST_CHECKLISTS.md#core-i-014), [CORE-I-015](TEST_CHECKLISTS.md#core-i-015), [CORE-I-038](TEST_CHECKLISTS.md#core-i-038) |
| [SystemCapabilitiesController.cs](src/Shared/Modules/Api/Features/System/SystemCapabilitiesController.cs) | [CORE-U-002](TEST_CHECKLISTS.md#core-u-002), [CORE-I-032](TEST_CHECKLISTS.md#core-i-032), [CORE-I-034](TEST_CHECKLISTS.md#core-i-034) |
| [UserNotesController.cs](src/Shared/Modules/Api/Features/Notes/Controllers/UserNotesController.cs) | [CORE-U-026](TEST_CHECKLISTS.md#core-u-026), [CORE-I-025](TEST_CHECKLISTS.md#core-i-025), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [UserPlaylistsController.cs](src/Shared/Modules/Api/Features/Playlists/Controllers/UserPlaylistsController.cs) | [CORE-U-026](TEST_CHECKLISTS.md#core-u-026), [CORE-I-025](TEST_CHECKLISTS.md#core-i-025), [FULL-I-005](TEST_CHECKLISTS.md#full-i-005) |
| [WorkersController.cs](src/Shared/Modules/Api/Features/Workers/Controllers/WorkersController.cs) | [CORE-U-039](TEST_CHECKLISTS.md#core-u-039), [UI-CORE-E-031](TEST_CHECKLISTS.md#ui-core-e-031) |

## Maintaining the two guides

Before marking an item complete, follow its backlink to the checklist and attach its evidence manifest. When splitting a broad scenario, keep the original ID as a parent or replace it with explicit cross-references; do not silently reuse IDs for different behavior. New features, endpoints, supported providers and routes require new cases or named variants and updates to both maps. Maintain source links after code moves. Plans and historical evidence are separate: never pre-check a row because a previous phase report lists a passing count.
