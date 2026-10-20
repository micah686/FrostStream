# Frontend Lite E2E evidence

<a id="ui-lite-e-001"></a>
## UI-LITE-E-001 — Passed

Fresh browser context opened `/profile`, `/library`, and `/admin` as local Admin without an external login prompt. See the per-test Playwright report in the run bundle.

<a id="ui-lite-e-002"></a>
## UI-LITE-E-002 — Passed

Canonical and legacy access-management routes redirected to Lite admin pages, with no access-management API requests.

<a id="ui-lite-e-003"></a>
## UI-LITE-E-003 — Implemented/unverified

Storage, import, metadata, schedules, workers, stats, and backups pages loaded without a Full-only permission denial. The guide requires using these features, so workflow functionality remains unverified and the checklist item stays open.

<a id="ui-lite-e-004"></a>
## UI-LITE-E-004 — Implemented/unverified

The browser created and verified a SQLite snapshot, showed Lite offline-recovery guidance, and exposed no differential option or Full restore-console link. Other backup types and restore controls were not all inspected, so the checklist item stays open.

<a id="ui-lite-e-005"></a>
## UI-LITE-E-005 — Implemented/unverified

A controlled 503 at the owned session proxy produced a visible error without Authentik/login navigation; recovering the proxy restored the local session. The guide calls for a Lite host/session startup failure; the host itself stayed running, so the checklist item stays open.

<a id="ui-lite-e-006"></a>
## UI-LITE-E-006 — Implemented/unverified

The run proved a UI-created playlist survives a real Lite process restart. The guide also requires media playback, resume position, and searchable/playable media consistency; this case does not exercise those assertions, so its checklist item remains open.

<a id="ui-lite-e-007"></a>
## UI-LITE-E-007 — Implemented/unverified

The run proved that an offline restore keeps a pre-snapshot playlist and removes a later playlist. It does not seed UI-visible media or verify restored media, credentials, or stale search-index entries, so its checklist item remains open.

<a id="ui-lite-e-008"></a>
## UI-LITE-E-008 — Implemented/unverified

The run deleted the owned Lucene index, restarted Lite, waited for `ready`, and completed a successful empty-result search. No persisted matching media was seeded, so result recovery against persisted media remains unverified and the checklist item remains open.

## Run details

The durable local [run manifest](../../../../../.artifacts/testing/frontend-lite-20261020T061301Z/manifest.json) and [Playwright HTML report](../../../../../.artifacts/testing/frontend-lite-20261020T061301Z/report/index.html) are in `.artifacts/testing/frontend-lite-20261020T061301Z/`. The manifest records the run ID, tested checkout revision and working-tree implementation files, exact command, test IDs, dependencies, browser substitution, assertions, artifact paths, and cleanup result. Playwright retries were disabled; all eight selected cases ran once and passed. Only E-001 and E-002 meet the full checklist requirements in this run; the other six remain open for the gaps described above.

The runner used the installed `@playwright/test` 1.56.1 package with cached Google Chrome for Testing 153.0.8010.12 via `FROSTSTREAM_E2E_CHROMIUM_PATH`. That executable is not the browser revision normally installed by Playwright 1.56.1; this substitution is recorded in the manifest. The Lite backend and browser fixture were owned by the harness and cleaned up after the passing run.
