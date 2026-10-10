# FrostStream UI Testing Plan

Status: **Planned — this document specifies the suite; it does not imply that the tests are implemented.**

Repository inventory reviewed: 2026-10-10.

## 1. Goal and definition of coverage

Build a complete, maintainable UI testing suite for the shared FrostStream frontend and the Full restore console. Verify what users can do, the resulting persisted state, and the behavior when requests, authentication, background work, or infrastructure fail.

The suite has three explicit categories:

| Category | Responsibility | Execution |
| --- | --- | --- |
| Common | Shared components, pages, and user journeys | The same browser specifications run against both real Lite and Full environments. |
| Lite | Automatic Admin access, SQLite persistence, local processing, and SQLite recovery | Dedicated Lite specifications plus all applicable Common specifications. |
| Full | Authentication, multiple users, authorization, distributed processing, and PostgreSQL/OpenBao recovery | Dedicated Full specifications plus all applicable Common specifications. |

Common is a reusable suite, not a third deployment. A Common test passing against one deployment does not count as coverage of the other.

Coverage includes functional browser journeys, component behavior, frontend logic, accessibility, visual regression, responsive layouts, recovery, and a manual device release checklist. Every supported page, redirect, significant control, capability branch, and user-visible mutation must have an assigned scenario. A screenshot or successful page load alone does not demonstrate that a feature works.

This document is the immediate deliverable. Installing dependencies, creating test harnesses, implementing specifications, fixing discovered product defects, and changing CI are subsequent implementation work described below.

### Agreed defaults

- Use comprehensive coverage, with a fast PR tier and exhaustive nightly/release tiers.
- Test actual backup restores in disposable environments, including the separate Full restore console.
- Automate casting UI with controlled device responses and supplement it with real-device release checks.
- Use real application backends for end-to-end journeys; isolate third-party Internet services from required CI.
- Preserve existing backend tests and deployment verification. Browser coverage supplements them.
- Full requires Authentik and OpenFGA. Former Full single-user settings require startup rejection coverage beginning in Phase 2.

## 2. Current state and sources of truth

The shared frontend is in `src/App/SharedApp/Frontend`: Svelte 5, SvelteKit 2, Vite 8, TypeScript, Tailwind/DaisyUI, Video.js 10, JASSUB, Chart.js, and virtualized lists. The package declares Node >=22 and pnpm 10.28.1; CI currently uses Node 22.

The route inventory contains **58 page source files representing 57 distinct route patterns**. `/admin` has both a page component and a redirect loader. Section 6 accounts for all 57 patterns. Shared layouts and components are tracked separately.

Existing checks:

- `tests/lite-access.test.mjs`: capabilities, session behavior, access redirects, and the API authentication/CSRF boundary.
- `tests/backup-capabilities.test.mjs`: server-rendered backup presentation for both deployments.
- `pnpm check` and `pnpm build`: frontend type and build verification.
- `Tests/UnitTests` and `Tests/IntegrationTests/IntegrationTests`: backend coverage, including Lite runtime, persistence, transport, storage, permissions, and recovery behavior.
- `.github/workflows/release-verification.yml`: shared checks, packaging, generated profile validation, and fresh-install smoke checks.

These do not constitute a browser suite. No implemented browser-test counts are claimed here.

Important implementation sources:

- `src/App/SharedApp/Frontend/src/routes/+layout.ts` and `src/lib/frontendAccess.ts`: capabilities, session loading, and frontend access decisions.
- `src/App/SharedApp/Shared/Deployment/SystemCapabilities.cs`: deployment capability contract.
- `Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeFixture.cs`: deterministic acquisition with real Lite composition, SQLite, Typesense, and FFmpeg.
- `src/App/FullApp/BackupService/Components/Pages`: the separate Blazor restore UI.
- `src/App/FullApp/AppHostFull/configs/authentik/blueprints/froststream.yaml`: Full login integration and branding.

Capabilities determine behavior. Do not infer Lite from single-user authentication, a hostname, a port, or the absence of a login button.

## 3. Libraries and responsibilities

| Dependency/tool | Use | Boundary |
| --- | --- | --- |
| `@playwright/test` | End-to-end browser tests, projects, browser/device profiles, screenshots, traces, and HTML/JUnit reports | Primary application and restore-console browser runner. |
| `vitest` | Frontend logic, state, formatting, validation, and capability tests | Node project for pure logic; browser project for Svelte behavior. |
| `@vitest/browser-playwright` | Run Vitest component tests in a real browser | Chromium in the PR tier; broader browser integration belongs to Playwright E2E. |
| `vitest-browser-svelte` | Render Svelte components, interact, rerender, and clean up | Use actual components and browser DOM behavior. |
| `msw` | Controlled component-test HTTP responses | Loading, empty, error, conflict, and permission states; never substitute for backend verification in real E2E journeys. |
| `@axe-core/playwright` | Automated accessibility scans | Combine scans with explicit keyboard/focus checks and manual assessment. |
| `@vitest/coverage-v8` | Frontend source coverage reporting | Identify untested branches; coverage percentages do not replace the scenario manifest. |
| Existing .NET/container tooling | Seed data, provision dependencies, control lifecycle, and verify persistence | Reuse repository conventions and production service composition. |
| FFmpeg/FFprobe | Generate small video/audio fixtures and process actual media | No dependence on downloading sample media from public sites during tests. |

Use Playwright's built-in screenshot assertions rather than an additional paid visual service. Do not introduce Jest, Cypress, Selenium, or a second component runner for the same responsibilities.

At implementation time, resolve stable package versions compatible with the existing Svelte/Vite/Node toolchain, pin them in the pnpm lockfile, and record the resolved versions. Keep Vitest packages aligned and Playwright browser binaries matched to the installed Playwright version. Validate a Svelte component, SvelteKit context fixture, and production player mount before extending the suite.

Official references supporting these choices:

- [Playwright projects](https://playwright.dev/docs/test-projects): shared specifications with separate environments and browser configurations.
- [Playwright authentication](https://playwright.dev/docs/auth): authenticated browser state and isolation.
- [Playwright visual comparisons](https://playwright.dev/docs/test-snapshots): screenshot baselines and controlled rendering environments.
- [Playwright accessibility testing](https://playwright.dev/docs/accessibility-testing): axe integration and the limits of automated checks.
- [Vitest Browser Mode](https://vitest.dev/guide/browser/) and [component testing](https://vitest.dev/guide/browser/component-testing): real-browser component tests.
- [vitest-browser-svelte](https://github.com/vitest-community/vitest-browser-svelte): Svelte rendering integration.
- [MSW browser integration](https://mswjs.io/guides/integrations/browser): deterministic browser HTTP handling.

## 4. Suite organization and interfaces

Keep frontend tests beside the frontend and put backend fixture tooling under `Tests/UIHarness`.

```text
src/App/SharedApp/Frontend/
  playwright.config.ts
  vitest.config.ts
  tests/
    unit/
    components/
      common/
      lite/
      full/
    e2e/
      common/
      lite/
      full/
    fixtures/
      data/
      media/
      http/
    support/
      auth/
      environment/
      pages/
      assertions/
    visual/
    coverage-manifest.ts
Tests/UIHarness/
  # Environment lifecycle, deterministic acquisition, seeding, recovery controls.
```

### Test classification

Every specification has a scenario ID and tags for category (`@common`, `@lite`, `@full`) and applicable tiers (`@smoke`, `@regression`, `@recovery`, `@a11y`, `@visual`, `@responsive`). Use an explicit `@controlled` tag for browser tests that intercept requests or simulate device responses. Report controlled tests separately from real-backend journeys.

Use stable ID families: `C-` for Common, `L-` for Lite, `F-` for Full, and `Q-` for cross-cutting quality. Split the coverage families below into atomic cases, such as `C-LIBRARY-001` and `F-AUTH-001`.

The coverage manifest records:

- Scenario ID, feature, exact route pattern or component, and user outcome.
- Category, layer, environment profiles, role, capability state, and CI tier.
- Fixture prerequisites and observable assertions.
- Implementing test path and implementation status: `planned`, `implemented`, `blocked`, or `accepted-exclusion`.
- Linked defect for blocked coverage; rationale, accountable maintainer, and review date for accepted exclusions.

A route inventory check compares all `+page.svelte` and `+page.ts` routes with the manifest, deduplicating identical route directories. It fails on an unmapped new route or a stale mapping. Component-only features, layouts, and the separate restore console need explicit entries too. During rollout, planned entries remain visible; final suite acceptance requires implementation or an explicitly accepted exclusion.

### Projects and test context

Playwright projects are `lite-{chromium,firefox,webkit}`, `full-{chromium,firefox,webkit}`, a Full configuration-rejection backend suite, and separate Lite/Full recovery projects. Each primary project selects Common plus its environment's specifications. Shared fixtures provide environment identity, base URL, role, run ID, seeded record IDs, capability expectations, and lifecycle controls.

Use explicit live-chat-on/off configuration profiles. Required capability tests must assert the expected capability and fail on configuration mismatch rather than silently skip.

The harness returns a run descriptor containing deployment mode, application URL, Full identity-provider URL, Full restore-console URL, test identities, seeded IDs, and owned resource identifiers. Sensitive values remain in temporary, ignored artifacts. No reset, seed, impersonation, or fault-control endpoint is added to the production API.

Small page/component helpers encapsulate repeated interactions. Keep business assertions in the specifications so a helper cannot hide a failed outcome.

### Proposed commands — not implemented yet

Run these from `src/App/SharedApp/Frontend` after the suite is implemented:

| Command | Required behavior |
| --- | --- |
| `pnpm test:unit` | Vitest pure-logic project. |
| `pnpm test:components` | Vitest browser component project. |
| `pnpm test:coverage` | Unit/component execution with coverage output. |
| `pnpm test:ui:common` | Common E2E specifications against both Lite and Full on Chromium. |
| `pnpm test:ui:lite` | Common + Lite functional specifications on Chromium. |
| `pnpm test:ui:full` | Common + Full functional specifications on Chromium. |
| `pnpm test:ui:smoke` | PR-critical browser cases for both deployments. |
| `pnpm test:ui:regression` | Complete functional browser matrix, compatibility, and capability profiles. |
| `pnpm test:ui:a11y` | Automated accessibility and keyboard specifications. |
| `pnpm test:ui:visual` | Compare approved screenshot baselines. |
| `pnpm test:ui:responsive` | Tablet/mobile/narrow layout and interaction cases. |
| `pnpm test:ui:recovery` | Provision dedicated disposable stacks and run restart/restore/PITR scenarios. |
| `pnpm test:ui:manifest` | Validate route mappings and scenario metadata. |
| `pnpm test:ui:report` | Open the most recent HTML report locally. |

Commands that require application environments provision owned test stacks by default. Allow attaching to an already provisioned harness run through its descriptor; do not make arbitrary existing user environments the default target. A browser-runner failure must propagate as a nonzero command exit, including after cleanup.

## 5. Common test rules

For each applicable form, list, and detail view, cover populated, empty, loading, invalid, missing-record, denied, conflict, timeout, and server-error states. Document genuinely inapplicable states rather than creating meaningless tests.

- Locate elements by role, accessible name, or label. Use test IDs for ambiguous nonsemantic controls only.
- Exercise user actions and observable results, not private component variables or CSS implementation details.
- Assert mutations through visible results, reload, and independent backend reads where appropriate.
- Test cancellation and repeated submission. Preserve inputs after a failed save and prevent accidental duplicate records.
- Verify paging/filter URL state, refresh, back/forward, and stale-response handling where the UI supports them.
- Use bounded observable waits for UI, API state, indexing, and job transitions. Do not use fixed sleeps or `networkidle` as a universal readiness check: realtime streams may remain connected.
- Fail on unexpected page errors and unhandled rejections. Expected failures must be narrowly identified by each scenario.
- Reset component mocks, SvelteKit context, stores, timers, local storage, and mounted components between tests.
- Keep actual playback clocks running. Freeze dates only in isolated presentation tests where doing so cannot prevent media or server work from progressing.
- Test documented/intended behavior. Missing implementation is a defect or blocked case, not a reason to weaken an assertion.

## 6. Common coverage catalog and complete route map

All families below run on Lite and Full with the appropriate authenticated identity. Full permission-denied and cross-user variants are additional Full cases. Paths are route patterns, not literal fixture URLs; dynamic segments must use valid seeded IDs, with separate missing/invalid-ID cases.

| ID family | Routes/UI | Required behaviors |
| --- | --- | --- |
| C-SHELL | `/`; global header/sidebar; root layout | Home statistics and links; capability/session bootstrap; active navigation; profile/admin links; mobile drawer; deep-link refresh; browser history; unknown-route error. |
| C-GLOBAL-SEARCH | Global search control | Debounce; keyboard selection and dismissal; loading/empty/error suggestions; outdated response suppression; result selection; all-results link. |
| C-LIBRARY | `/library` | Videos, Playlists, History, Liked; available filters/sorts; pagination; URL state; cards/artwork fallback; empty collections; downloaded and user playlist navigation. |
| C-CREATOR-DIRECTORY | `/library/creators` | Platform filter/apply/clear; cursor paging forward/back; refresh; empty/failure state; avatar fallback; channel links. |
| C-CREATOR-MONITOR | `/creators` | Add/edit/remove source; configuration and monitoring options; interval/range validation; duplicate prevention; incremental/full scans; downloads; discovery status and errors. |
| C-CHANNEL | `/channel/[accountId]` | Account details, media filters/paging, available tabs, statistics, discovery actions, target notes, external links, artwork fallback, missing account. |
| C-CHANNEL-AUDIO | Channel audio controls | Audio queue selection/advance; playback controls; podcast feed creation/presentation/copy/open; unavailable audio; permission and request failures. |
| C-SEARCH | `/search` | Available media/channel/comment/caption scopes; query/sort/page URL state; highlights; empty results; similar-media search; open result and timestamp where supported. |
| C-ADVANCED-SEARCH | `/search/advanced` | Every exposed field/prefix; text escaping; numeric/date/duration ranges; incompatible or invalid values; reset; generated query and result navigation. |
| C-WATCH | `/watch/[mediaGuid]` | Loading/not-found/unavailable; metadata tabs; descriptions/comments/replies; chapters; source version/storage selection; more-actions menu; likes; notes; playlist saving; up-next/autoplay. |
| C-PLAYER | Watch video and channel audio players | Actual decode/play/pause/seek; mute/volume; speed; supported quality/rendition changes; buffering/errors; end-of-media; keyboard controls; disposal and navigation between media. |
| C-CAPTIONS | Watch captions/subtitles | Track/language selection; off state; native tracks; JASSUB-rendered subtitles; seek synchronization; missing/failed tracks; no archived captions. |
| C-FOCUS | Watch focus/fullscreen controls | Enter/exit focus; fullscreen entry/exit; focus-to-player-fullscreen handoff; Escape; layout/focus restoration; supported picture-in-picture controls where exposed. |
| C-WATCH-STATE | Watch and library history | Save/resume progress; mark watched; history update; refresh persistence; replay; stale saves during media navigation; completion boundaries. |
| C-CAST | Browser/server casting controls | Protocol/device menus; no devices; discovery/refresh; connect/attach; failed connect; play/pause/seek/volume; live status; disconnect; closed-menu and unmounted-component cleanup. |
| C-CHAT | Watch chat replay | Capability on/off; absent replay; loading/error; synchronize to playback; seek forward/back; auto-scroll/manual scroll; fragments/emotes/links; large history; avoid repeated requests for unavailable replay. |
| C-DOWNLOAD | `/download` | Every download tab; single/multiple URLs; playlist handling; config/storage/worker selection; overrides; invalid input; pending/failed submit; duplicate clicks; queued-this-session results. |
| C-JOBS | `/jobs` | Source/status/search filters; paging/page size; groups and expanded rows; progress updates; supported stop/retry/other actions; failed/completed/interrupted states; reconnect without duplicate rows. |
| C-ENCODING | `/jobs/encoding` | Kind/status/storage/query filters; paging; real rendition progress; watch links; stop/retry; terminal states and errors. See known gap in section 12. |
| C-BACKGROUND | `/jobs/background` | Runs and details; filtering; expanded progress; completion/failure; realtime updates; empty state; cleanup after navigation. |
| C-PROFILE | `/profile`; profile layout | Identity and overview; section links/active state; Admin/Owner/signed-in labels; logout visibility; session failure presentation. |
| C-CONFIG-SETS | `/profile/config-sets`, `/profile/config-sets/new`, `/profile/config-sets/[key]` | List/create/edit/delete; referenced presets/storage; defaults and validation; missing references; save failure; cancel; reload persistence. |
| C-OPTION-PRESETS | `/profile/option-presets`, `/profile/option-presets/new`, `/profile/option-presets/[key]` | List/create/edit/delete; option editor sections; search where offered; tri-state values; value serialization; validation and persistence. |
| C-COOKIES | `/profile/cookie-management`, `/profile/cookie-management/new`, `/profile/cookie-management/[key]` | List/create/edit/delete; all supported cookie input modes; invalid input; secret masking; replacement/update behavior; error and persistence. |
| C-NOTIFICATIONS | `/profile/notifications`, `/profile/notification-providers/new`, `/profile/notification-providers/[key]` | Preferences; provider listing/create/edit/delete; every exposed provider form and required field; test-delivery result; failure; secret masking and persistence. |
| C-PLAYLISTS | `/profile/playlists`, `/profile/playlists/new`, `/profile/playlists/[playlistId]` | List/create/edit/delete; search/add/remove/reorder items; duplicate handling; save-to-playlist button; playlist panel; watch order; empty/missing lists. |
| C-NOTES | `/profile/notes`; target note panels | Create/read/edit/delete; target selection/navigation; correct media/channel binding; empty/error states; save/reload. |
| C-APPEARANCE | `/profile/appearance`, `/profile/appearance/custom-css` | Select/persist every built-in theme; keyboard selection; CSS designer and raw editor; copy-from-theme; save; enable/disable custom CSS; reload; reset of test browser state. |
| C-STORAGE | `/admin/storage`, `/admin/storage/new`, `/admin/storage/[key]` | List/detail/create/edit/delete; view switching; all protocol/provider/credential branches; validation; masked secrets; worker tags; conflicts; failure and persistence. |
| C-STATISTICS | `/admin/stats` | Overview, inventory, activity, coverage, channel and top lists; date ranges/intervals; channel lookup/select/clear; chart updates; zero/missing data; accessible values. |
| C-METADATA | `/admin/metadata` | Search reindex; database maintenance reindex; media lookup; storage selection for deletion; invalid/missing GUID; confirmation/cancel; progress/success/failure. |
| C-IMPORT-LIST | `/admin/import`, `/admin/import/new/source`, `/admin/import/[sessionId]` | Sessions and statuses; new source; worker/storage/folder selection; folder picker; invalid/unavailable path; existing session entry/navigation. |
| C-IMPORT-FILES | `/admin/import/[sessionId]/files` | Scan results; include/exclude; unsupported/duplicate files; probe results/failures; selections persist across steps. |
| C-IMPORT-MAPPING | `/admin/import/[sessionId]/mapping` | Download example/template; upload valid/invalid mapping; errors with useful file context; correct mapping to selected items. |
| C-IMPORT-METADATA | `/admin/import/[sessionId]/metadata` | Edit/enrich supported metadata; validation; missing required data; save/error; preserved edits. |
| C-IMPORT-REVIEW | `/admin/import/[sessionId]/review` | Accurate summary and exclusions; blocked invalid commit; repeated-click handling; back/edit/review consistency. |
| C-IMPORT-RUN | `/admin/import/[sessionId]/run` | Commit and progress; completed/skipped/failed items; partial failure; available cancel/retry; refresh/resume; completed media appears and plays. |
| C-WORKERS | `/admin/workers` | Names/tags/incoming folder; tag filtering where offered; heartbeat freshness; offline/unavailable/empty state; refresh. |
| C-SCHEDULES | `/admin/schedules`, `/admin/schedules/[key]` | Create/edit/delete; supported task types/arguments; cron/time validation; enable/disable; run feedback; persisted schedule; capability-dependent backup tasks. |
| C-BACKUPS | `/admin/backups` | Repository health/empty state; supported options; start backup; progress/history/details; verification; errors; correct recovery guidance/link. |
| C-REDIRECTS | `/admin` | Redirect to `/admin/storage` on direct load and navigation. |
| C-LEGACY-REDIRECTS | `/admin/access-management`, `/admin/bundle-management`, `/admin/media-access` | Redirect to access control in Full; correctly complete Lite's redirect chain without loops. |

### Shared components and non-route state

Component specifications cover `Select`, `RangeSlider`, `Modal`, `Drawer`, `Stepper`, `ConfirmDeleteModal`, `FolderPickerModal`, `ImportWizardStepper`, and `ImportNotice`; playlist/note panels and buttons; job/background rows; chart presentation; comments/chat fragments; configuration/provider forms; the option editor and tri-state select; player/cast menus.

Assert focus trapping/restoration where appropriate, Escape/backdrop behavior, labels, keyboard selection, disabled/busy state, selected state, emitted actions, and cancellation. Test chart data and accessible text separately from pixel snapshots. Test virtualized lists by interacting with offscreen items rather than assuming all items exist in the DOM.

Frontend logic tests cover API error parsing, capability decisions, request authentication/CSRF handling, query construction, form serialization, date/number/media formatting, pagination state, watch-state updates, theme persistence, and chat playback synchronization. Use browser component tests for Svelte reactivity and lifecycle behavior.

### Storage and provider boundaries

Enumerate form cases from the production definitions so new variants require new cases:

- Local.
- Network: FTP, FTPS, SFTP, NFS, SMB, CIFS.
- S3-compatible: AWS S3, MinIO, DigitalOcean Spaces.
- Azure Blob: account key, connection string, SAS URL.
- Google Cloud Storage: credentials JSON, credentials file path, workload identity, default credentials.
- Every notification provider exposed by its production catalog.

Verify visible fields, required/optional values, secret handling, submitted payload, error mapping, and edit round-trips for every variant. Use real Local storage in core E2E; add MinIO and SFTP container-backed journeys nightly. Other external providers receive deterministic UI/contract coverage, not a claim of cloud-service certification. Notifications use a local delivery sink; never send to actual recipients in required CI.

## 7. Lite-specific suite

| ID family | Scenario and assertions |
| --- | --- |
| L-BOOT | New browser reaches library/home as Admin without external login; capability reports Lite; supported admin sections remain usable. |
| L-ACCESS | Permission-dependent shared controls remain available under Lite semantics; no sign-out/external login affordance; access-control nav absent. |
| L-ACCESS-URL | Direct `/admin/access-control`, bundle new/edit, policy new/edit, and legacy URLs redirect through the documented chain to supported administration without loops. |
| L-SESSION-ERROR | Failed session/capability fetch produces an error; a Lite 401 does not trigger an external authentication redirect loop. |
| L-REQUESTS | Mutations avoid Full-specific CSRF/login requests; validation and other HTTP failures still reach the UI. |
| L-PERSISTENCE | Saved settings, playlists, notes, completed media, and terminal jobs remain correct after process restart with the same SQLite file. |
| L-DURABLE-WORK | Real SQLite queue/workflow processing drives download/import UI; restart/abrupt interruption displays the reconciled outcome; explicit retry creates a new run as intended. |
| L-DEPENDENCIES | Local Worker/MediaProcessor/Quartz behavior works with Typesense through Phase 4 and embedded Lucene from Phase 5, plus configured optional services; observe that Full-only services are not contacted. |
| L-BACKUP-CAPABILITIES | SQLite snapshot wording; full/verification supported; differential, PITR, and deep-verification actions absent; schedules omit unsupported types. |
| L-BACKUP-VERIFY | Create snapshot via UI; see completed result; verify it; corrupt only an owned fixture recovery set and verify useful failure presentation. |
| L-RESTORE | Back up known records and a secret-dependent configuration, mutate them, stop Lite, restore snapshot plus complete key ring using documented offline steps, restart, then verify restored records and secret-dependent behavior in the UI. |

The same SQLite database contains application records and durable messaging/workflow state. Include these in recovery expectations, while respecting startup reconciliation: do not assume interrupted jobs automatically resume. Keep media fixture files in retained test storage; database backup tests must not imply media bytes are included in a database snapshot.

## 8. Full-specific suite

### Authentication, authorization, and user isolation

| ID family | Scenario and assertions |
| --- | --- |
| F-AUTH | Real Authentik login; invalid credentials; successful callback; return to original path/query; logout; protected deep links; expired session and refresh behavior. |
| F-LOGIN-UI | FrostStream-branded login layout, keyboard/labels, small viewport, loading/error feedback; no assertion that unrelated Authentik administration is covered. |
| F-ROLES | Administrator, restricted user, authenticated ungranted user, and anonymous contexts; correct outcomes for visible controls, direct URLs, and actual underlying requests. |
| F-CSRF | Real browser mutation succeeds with valid protection; missing/invalid CSRF fails without a persisted change; failures are presented correctly. |
| F-USER-ISOLATION | Private playlists, notes, cookies, notifications, history, favorites, and other user-scoped configuration cannot leak across users; deliberately shared objects follow their configured policy. |
| F-PERMISSION-CHANGE | Grant/revoke while a browser is active; reload/revalidation and subsequent operations respect the new permission; previously visible content does not bypass server enforcement. |
| F-SINGLE-USER | Owner identity and hidden access-management/sign-out UI as specified; Full capabilities/backups/transport remain Full; never classify this profile as Lite. |

Additional Full route map (these also receive Lite redirect cases):

| ID family | Routes | Required behaviors |
| --- | --- | --- |
| F-ACCESS-CONTROL | `/admin/access-control` | Policy listing/search/selection; principal and resource scope; allow/deny; assignments; effective-access inspector and resource check; invalid/conflicting rules; denied operations. |
| F-POLICY-EDITOR | `/admin/access-control/policies/new`, `/admin/access-control/policies/[policyId]/edit` | Redirect to the appropriate `view`/`policyId` query; create/edit/delete and validation in the actual editor; cancel/back behavior; missing policy. |
| F-BUNDLES | `/admin/access-control/bundles/new`, `/admin/access-control/bundles/[bundleId]/edit` | Catalog search and endpoint selection; create/edit/delete runtime bundles; protected built-ins; duplicates/validation; effects on assigned permissions. |

Assert real backend enforcement, not just disabled buttons. A restricted user directly issuing a forbidden operation through its authenticated browser/API context must receive denial and cause no mutation.

### Distributed operation and recovery

| ID family | Scenario and assertions |
| --- | --- |
| F-DISTRIBUTED | Real Full modules, NATS, PostgreSQL, Typesense, and workers complete acquisition/import/processing; UI shows the durable result after refresh. |
| F-WORKER-RECOVERY | Worker/event connection failure and restart; bounded recovery; accurate interrupted/retried states; no duplicated displayed results. |
| F-BACKUP | Full/differential options actually exposed by the UI; repository health; quick/deep verification; jobs/errors; OpenBao pairing; correct restore-console link. |
| F-RESTORE-AUTH | Separate console root/login: unauthenticated redirect, wrong/empty/correct restore token, protected access, and session handling. |
| F-RESTORE-PREREQUISITES | Running PostgreSQL, missing/unhealthy repository, no backups, unwritable volume, stale PID; continuation blocked appropriately; re-check and stale-PID action on owned fixtures. |
| F-RESTORE-SELECTION | Available backup choices, PITR mode/time validation, selected summary, back/next, exact stanza-name confirmation, prevention of duplicate start. |
| F-RESTORE-PROGRESS | Execute/progress/errors/completion; reload behavior and server-side job continuity; useful recovery from failure. |
| F-RESTORE-OPENBAO | Paired snapshot recovery; unavailable/failed snapshot; export fallback; verify a secret-dependent application operation rather than only a success message. |
| F-RESTORE-ACTUAL | Create backup through main UI, modify known data, stop services per recovery prerequisites, complete restore through console, restart, sign in, verify expected records and absent post-backup changes. |
| F-PITR-ACTUAL | Establish known transactions before/after a target and ensure WAL is archived; restore through console to that target; verify included/excluded records and successful application use. |

Resolve the restore console URL from deployment output; its page root `/` and `/login` are on a separate service. Preserve its real server-interactive behavior. HTTP interception in the browser cannot fake server-injected restore services; negative cases must use owned fixture state or a dedicated test-only server composition.

Real restore tests must account for service dependencies: preserve the backup service's independent access while the other services are stopped, restart PostgreSQL after restore, restore the paired vault state with OpenBao running/unsealed, then verify application readiness and authentication. Rebuild derived indexes as required by the recovery procedure before checking search. Store media bytes independently and retain them for playback verification.

The Full capability contract advertises incremental support, but tests must separately inventory which choices the page exposes. Do not claim an incremental UI action is tested if none exists; record any product expectation mismatch.

## 9. Cross-cutting quality suites

### Accessibility

Run axe checks on representative loaded pages and on interactive states such as open dialogs, expanded menus, validation errors, and wizard steps. Target WCAG 2.1 A/AA rules supported by the scanner; supplement with explicit keyboard/focus assertions, zoom/reflow, reduced motion, and manual screen-reader checks. Automated checks do not establish complete accessibility conformance.

Test navigation and submission without a mouse; visible focus; labels and error associations; dialog focus trap/restoration; tab semantics; announcements for validation/progress; text contrast; chart alternatives; player controls and caption selection. Each scan must await the intended UI state. Do not exclude an entire player, dialog, or route to obtain a passing report.

New violations fail the applicable tier. Existing violations become named defects with narrowly scoped, reviewed temporary exceptions and expiry dates; they cannot disappear from the report.

### Browsers and responsive layouts

- Desktop reference: 1440×900.
- Tablet: 768×1024.
- Mobile: 390×844, including touch interaction.
- Narrow viewport: 320px width for reflow and horizontal overflow checks.
- Chromium, Firefox, and WebKit desktop functional projects nightly/release.
- Responsive Chromium and mobile WebKit journeys nightly/release; mobile drawer/navigation smoke on every PR.

Emulation is not real-device coverage. When a browser cannot provide a native feature, test its unsupported-state behavior and retain a manual real-device case for supported targets. Codec, fullscreen, and casting exceptions must be explicit in the manifest, not blanket skips.

### Visual regression

Use pinned Linux/browser/font versions and deterministic seeded data. Capture default, light, and dark presentation across representative shell, library, search, watch, jobs, settings, import, administration, Full login, and restore-console states. Cover all built-in themes functionally; avoid an unbounded every-theme × every-route screenshot matrix.

Keep Lite and Full baselines separate where labels, actions, or layout differ. Use representative mobile/tablet baselines. Wait for fonts and fixtures, disable animations, and mask timestamps/unstable media pixels only where necessary; keep player controls visible. Stabilize canvases or test their data independently. Baseline updates are explicit reviewable changes and never run automatically in CI.

### Resilience, scale, and content safety

- Disconnect/reconnect HTTP and realtime streams; verify clear status and recovery without duplicate subscriptions or rows.
- Navigate repeatedly among watch, jobs, chat, and cast UI; verify old requests and updates cannot affect a new page/media item.
- Seed enough records to cross every pagination boundary; include dedicated large library, job, playlist, comment, and chat cases.
- Verify virtualized offscreen items can be reached and selected, and requests/DOM growth remain bounded by the component's paging/virtualization contract.
- Record navigation, search, and player-ready timing on a fixed CI fixture. Initially report timings; set measured regression budgets after baseline runs rather than inventing unsupported performance guarantees.
- Test untrusted titles, descriptions, notes, comments, filenames, mapping contents, and long/Unicode strings for safe rendering and usable layout.
- Assert secret values are neither echoed into rendered fields nor returned as readable existing secrets. Scrub logs/artifacts as well.

## 10. Environment harness and fixture design

### Provisioning and ownership

Use unique test project names, networks, ports, volumes, directories, and run IDs. Each independently executing project/shard owns its stack. Execute stateful cases serially within a stack; parallelize with additional isolated stacks. Lite's single Admin identity means separate browser contexts alone do not isolate persisted mutations.

Before browser work, verify service readiness, capabilities, seeded identities, and required search/index state. Full setup includes real Authentik and OpenFGA; Full E2E must not rely on the self-issued-token shortcut used by some backend tests. Provision test users/groups through test bootstrap tooling and establish reusable browser state through the actual login flow.

Never invoke existing volume-removing smoke scripts against a developer's deployment. Adapt their assertions into an ownership-aware harness. Teardown may remove only resources listed in the current run descriptor. Restore/fault tests use separately owned stacks; capture logs before teardown, including on startup failures.

### Application execution

Serve built frontend assets with the actual proxy paths for `/api`, `/auth`, media streaming, event connections, and Full restore navigation. `vite preview` alone is insufficient evidence of production proxy correctness. Packaged-release smoke uses the generated deployment profiles and candidate images.

Reuse the Lite runtime fixture's deterministic acquisition boundary in a standalone browser-accessible harness. For Full, provide a test-only Worker composition with the same acquisition replacement while retaining real distributed services. Package verification additionally exercises production acquisition against a controlled local fixture origin so the installed binaries and image wiring are checked.

Only Internet acquisition and binary provisioning are replaced in the core deterministic journeys. Use real FFmpeg/FFprobe, storage writes, database persistence, queue/workflow handling, indexing, and media delivery. Provide outbound notification sinks and controlled device responses instead of real external recipients/hardware in CI.

### Fixture inventory

| Fixture group | Required data |
| --- | --- |
| Installations | Empty installation and populated reference library; live chat disabled/enabled; Full Authentik and obsolete-setting rejection. |
| Media | Small generated video/audio, actual renditions, thumbnails, multiple versions/storage locations, native and rendered captions, chapters, comments, and chat replay. |
| Edge media | Missing artwork/media, unavailable rendition, invalid caption, long/Unicode metadata, malicious-looking plain text, and unsupported content. |
| Collections | More than one page of library/search/creator/job/playlist data; explicit large-dataset fixtures. |
| Jobs | Pending, running, failed, canceled/stopped, completed, and interrupted outcomes as defined by production types. |
| Identity | Admin, restricted, ungranted, anonymous, and two independent ordinary users with private data and selected shared resources. |
| Configuration | Presets, config sets, cookies, providers, schedules, and each storage-form variant. |
| Import | Valid directory, nested items, duplicate/unsupported files, valid/invalid mapping JSON, partial failure, and incomplete metadata. |
| Recovery | Pre/post-backup record markers, known secret-dependent configuration, paired recovery artifacts, and pre/post-PITR transactions. |

Create prerequisites through existing APIs or a test-only .NET seed command. Use direct persistence seeding only for states that cannot reasonably be created through workflows; those fixtures cannot count as evidence that the bypassed workflow works. Assertions for the behavior under test must come from actual UI interaction and resulting application state.

Reset scenario-owned data between tests and after retries. Keep an immutable reference fixture plus scenario-specific identifiers; a retry cannot reuse a half-mutated record. Ensure readiness waits include background indexing, not just process liveness.

### Mock boundaries

- Component tests: MSW handlers for HTTP; explicit SvelteKit page/navigation context; controlled timer/state fixtures.
- Controlled browser cases: narrowly intercept the named request or use a deterministic device adapter to exercise errors and rare states. Reject unexpected application requests in fully controlled cases.
- Real E2E cases: application API, authentication, authorization, data, jobs, and media paths are not intercepted.
- Server-interactive restore cases: use real fixture state or test-server injection, not a pretend browser API response.
- Fault injection: restricted to owned infrastructure; no production behavior switch or public test endpoint.

## 11. CI, reporting, and release device checks

| Tier | Mandatory execution |
| --- | --- |
| Every PR | Existing backend tests; frontend check/build; route manifest; unit/components; Chromium critical Common journeys on Lite and Full; primary auth/permission/capability cases; mobile navigation; critical accessibility and visual checks. |
| Nightly | Complete Common/Lite/Full functional browser matrix; Full obsolete-setting rejection; live-chat on/off; responsive/accessibility/visual suites; large data and reconnect cases; real restart, SQLite restore, Full restore and PITR. |
| Before release | Nightly coverage against the candidate revision; candidate packaged deployment/proxy/media smoke; actual restore/PITR; recorded manual device results. |

Add a nightly schedule and dedicated UI jobs to release verification. Keep package architecture checks on the existing amd64/arm64 matrix; run the exhaustive browser matrix on the pinned Linux reference runner and browser smoke against each candidate architecture. Release publication must depend on required automated UI results as well as existing verification.

The `@smoke` set must include: startup and navigation; Full login/restricted denial; library/search/real playback; persisted profile mutation; download through completion; import through playback; and each deployment's backup capability presentation. These are end-to-end assertions, not mocked success responses.

Start with a 45-minute PR browser-job timeout and a 120-minute nightly/recovery-job timeout. Bound readiness/job waits separately and report their phase on failure. Optimize sharding after measuring execution time; do not silently drop required scenarios to meet the timeout.

### Results and flaky tests

Publish HTML/JUnit reports, scenario/environment/browser identifiers, failure screenshots, Playwright traces, browser errors, and relevant service logs. Retain failure videos for media/interaction cases when useful. Keep artifacts for 14 days and omit credentials, cookies, auth storage-state files, private keys, and raw secret-bearing fixtures.

Allow one CI retry for diagnosis. Report first-attempt failures even when the retry passes. A flaky result fails release acceptance; PR/nightly flakiness also remains visible and requires a tracked fix. Do not increase retries to hide nondeterminism.

An exception requires a scenario ID, linked issue, maintainer, reason, and expiry/review date. Required restore/auth/isolation coverage cannot be silently skipped because setup failed. Distinguish infrastructure failure, product failure, and test defect in reports while keeping the job unsuccessful.

### Manual release checklist

Record build revision, deployment, device/OS/browser or casting protocol, tester, result, and linked issue. Run these on both deployments where applicable:

- Real iOS Safari and Android Chrome: navigation, touch controls, playback, seeking, captions, rotation, fullscreen, and resume behavior.
- Each casting protocol actually exposed by the product: discovery, connection, playback, seek/volume, disconnect, and reconnection on real receivers.
- Keyboard-only main journeys and a screen-reader pass through navigation, forms, dialogs, job feedback, and player controls.
- Full authentication and restore-console layout/accessibility on the reference desktop browser.

Device unavailability is an explicit release exception, not a passing test. Manual results are release evidence; browser emulation does not replace them.

## 12. Implementation sequence, known gaps, and acceptance

### Sequence

1. **Foundation:** library/config compatibility proof, environment ownership/lifecycle, deterministic fixtures, built frontend serving, route manifest, reports, and equivalent migration of existing frontend assertions.
2. **Core Common journeys:** shell, library, creators, search, watch, real video/audio, captions, history, playlists, notes, and profile configuration.
3. **Operations:** download/jobs/encoding/background, imports, storage, metadata, workers, statistics, schedules, and backups.
4. **Environment behavior:** Lite access/persistence/reconciliation, Full real login/roles/permissions/isolation/distributed recovery, restore console, actual restores/PITR.
5. **Quality and enforcement:** complete responsive/visual/accessibility matrix, large-data/reconnect checks, device checklist, CI tiers, and final manifest reconciliation.

Each phase adds executable cases and meaningful assertions; no phase is complete solely because a test file or page helper exists. Preserve the existing access/backup test commands until equivalent assertions pass under the new suite and CI is updated atomically.

### Known gaps and implementation hazards

| Finding | Required treatment |
| --- | --- |
| Encoding Stop and Retry buttons currently render without click handlers in `/jobs/encoding`. | Track as a product gap; require action/result tests when wired. Rendering alone cannot pass C-ENCODING action coverage. |
| Full capabilities advertise incremental backups, while current backup UI options must be checked separately. | Test the exposed UI and record any intended-but-missing operation explicitly. |
| Restore console says server-side work survives reload. | Verify both job continuity and visible reconnection; do not assume current implementation fulfills the promise. |
| Existing integration auth tests can use self-issued bearer tokens. | Preserve them, but implement actual browser login for Full E2E. |
| Existing frontend checks are logic/SSR-focused. | Retain their assertions and add real interaction coverage. |
| Existing deployment smoke tooling removes profile volumes. | Do not use it on user environments; require explicit harness ownership before cleanup/recovery. |
| Optional chat, codecs, fullscreen, and casting have distinct dependencies. | Declare profile/device prerequisites; missing prerequisites cannot silently erase required coverage. |

### Suite completion criteria

- Every route in sections 6 and 8, shared layout, and significant UI control has a manifest assignment and passing behavior coverage or an explicitly accepted exclusion.
- Every Common journey passes on both actual Lite and Full backends; environment identity is asserted at setup.
- Every exposed mutation verifies its result, applicable validation/failure behavior, and relevant persistence after reload.
- Full authorization is validated with direct requests and multiple users, not only UI visibility.
- Real video/audio delivery and processing are exercised, with named device/browser limitations.
- Both backup/recovery models are tested; Full actual restore/PITR and Lite snapshot/key-ring recovery pass in isolated stacks.
- New routes without coverage mappings fail CI. Planned or blocked required cases cannot be counted as implemented coverage.
- Required tiers have no unexplained skips, hidden flaky passes, or automatically accepted screenshot changes.
- Accessibility defects and manual device results remain visible release evidence.
- Known functional gaps are fixed or accepted explicitly with their remaining risk documented.
- A clean checkout can provision, execute, report, and clean up the suite using the documented commands without depending on an existing developer deployment.

### Documentation deliverable verification

Before handing off this plan, compare its route patterns with the current repository, verify cited local paths and library references, and check Markdown/patch formatting. Creating this document does not install the libraries, create the proposed commands, or make the planned scenarios pass.

## Architecture refactor verification overlay

All implementation and fixtures live under `src-refactor/`; paths above are relative to that directory. The copied baseline preserves existing behavior until the corresponding phase changes it. Full single-user acceptance tests are historical characterization, never a target compatibility requirement. Phase 2 replaces them with rejection of `SINGLE_USER_MODE` and `Auth:SingleUserMode`, required Authentik/OpenFGA configuration, real login, allow/deny and user isolation. Current Full integration identities are controlled; they do not prove real Authentik browser login.

Phase 0 evidence and outstanding gates are tracked in `Phase0/CHECKLIST.md`. Phase 4 introduces common provider search contracts, including notes isolation, every scope/filter/sort, paging, totals and deletion. Phase 5 runs the same contracts against Lucene and removes Lite Typesense fixtures. Relevance-score equality is excluded. Add durable index-intent replay, concurrent rebuild activation, failed rebuild preservation, corruption/restart and rebuild-status UI coverage.

Dependency and host boundaries are enforced incrementally against the Phase 0 inventory; do not accept new violations. Fresh installations use owned disposable state. No old-data migration or mixed-version rollout suite is required. Laptop measurements must include FFmpeg children, containers and filesystem cache; passing on a larger development machine does not satisfy the 8 GB gate.
