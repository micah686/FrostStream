# Lite browser E2E fixture

From `src/Frontend`, install the browser pinned by the lockfile with `pnpm exec playwright install chromium`, then run `pnpm test:e2e:lite`. By default Playwright starts the owned fixture automatically: a real Lite backend process, a Vite frontend using the fixture proxy, and a loopback-only lifecycle controller. No Full services or live internet are needed. The Lite runtime uses a unique temporary data root; successful teardown stops only its process groups and removes that marked root. On failure, the root and logs are retained for diagnosis.

To retain the fixture data explicitly, set `FROSTSTREAM_E2E_LITE_DATA` to a new, empty path. The controller writes an ownership marker there and refuses existing unmarked paths. Optional `FROSTSTREAM_E2E_LITE_HOST_PORT`, `FROSTSTREAM_E2E_LITE_CONTROL_PORT`, and `FROSTSTREAM_E2E_LITE_FRONTEND_PORT` settings select loopback ports; all three must be free.

The controller issues a per-run random bearer token and accepts only loopback requests. Session faults are injected as a temporary 503 at the real proxy boundary. Restart operates on the owned Lite child. Restore uses the real `SqliteRecoveryCommand` to publish the UI-created snapshot into a new destination, then starts Lite against that restored database and key ring. Lucene recovery removes only the owned index directory and restarts Lite so the normal authoritative rebuild runs. It never targets a developer installation.

Playwright retains traces, screenshots, video and an HTML report under `.artifacts/lite-e2e` by default. Keep these artifacts private because browser traces can contain session information.
