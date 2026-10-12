# Packaged Phase 0 verification — 2026-10-10

Current-code re-verification on 2026-10-11: standard Docker Compose 5.6.0 startup passes against the Podman API; podman-compose still fails. Distributed download/fresh import and original/range playback pass, but both HLS paths fail on internal storage HTTP 401. See [../verification/current-validation.md](../verification/current-validation.md) for scope, measurements and reproduction. The sections below retain the earlier baseline evidence.

The continuation built and ran the copied candidate packages on an initially empty rootless Podman engine. Projects are named `froststream-phase0-lite`, `froststream-phase0-lite-repeat` and `froststream-phase0-full`. These names isolate container/volume state. Full's backup and vault-bootstrap bind directories remain under `Full/docker-compose/`. No existing installation or volume was reset. Raw reports are in ignored `.phase0/results/`; the portable summary is in ../verification/evidence/phase-0-verification-results.json.

Lite's frontend serves HTML, forwards actual API requests, reports Lite/SQLite capabilities and authenticates the local Admin with the `admins` group. The checker rejects HTML returned for an API path, so a SPA fallback cannot produce a passing API result. These checks passed on two separate empty-state installations and after restarting the first backend. These are startup/authentication smoke checks, not packaged media execution or search-readiness measurements. Existing real-media Lite integration evidence remains separate.

Full built all application images. Its 15 long-lived containers were running at final verification, all four declared Compose health checks were healthy, and its four one-shot initializers exited zero. The browser checkpoint passed twice, each time creating two fresh Authentik identities and signing in through the real OIDC authorization-code flow. Both accounts have the FrostStream `admins` group: private ownership must still hold for privileged accounts. The checkpoint verifies:

- Anonymous session requests return 401; authenticated sessions report multi-user mode and survive a browser reload.
- Each user creates a private playlist through the frontend/API/NATS/DataBridge/PostgreSQL path.
- Neither user's list includes the other's playlist. Direct reads and updates of the other's playlist return 404 in both directions.
- Logout without CSRF returns 403. Valid logout clears the session, and replaying the captured old session cookie returns 401.
- Fixture playlists and Authentik identities are deleted after the checks. Application user-profile and authorization records may remain in the disposable installation.

No signing keys, handlers, database operations or authorization responses are substituted. Credentials and session cookies are held in memory, excluded from reports, and no browser traces are saved. These checks do not establish private-note/search parity or distributed Full download/import/playback.

## Engine and packaging findings

The installed `docker` command delegates to Podman and `podman-compose`. Standard Full `compose up` fails because this provider passes completed one-shot initializers as native `--requires` dependencies, which require running containers. Its default shared-pod mode also puts independent services in one network namespace, conflicting with their repeated port 8080 listeners. Standard Full startup remains an open gate.

`start-packaged.py` is an explicit Phase 0 workaround. It preserves the generated service settings, networks, volumes, relative paths and `.env` but starts individual services in dependency order with `podman-compose --in-pod=false`. Single-service temporary definitions omit `depends_on` so the runner can enforce completion/health without native `--requires`. Temporary files are private and removed afterward. The runner waits for required health/completion conditions, then checks every final service state. It only accepts project names starting with `froststream-phase0-`. Re-running startup recreates containers and retains state; use `--verify-only` for a read-only check. Podman Compose may print network-in-use messages while recreating a service; final container health and endpoint checks are recorded separately.

The package also had an independently reproduced OpenBao defect: `bao status` attempted `https://127.0.0.1:8200` against the configured HTTP listener and failed. `StartOpenBao.cs` now supplies `BAO_ADDR=http://127.0.0.1:8200`; the working generated Full profile was updated identically. The default command now succeeds and the container is healthy. Full AppHost builds with zero errors and the existing ASPIRE010 environment warning.

Podman warns that Dockerfile health checks are ignored for its OCI image format. The Lite checker therefore exercises endpoints directly. Full health evidence refers to explicit checks in the generated Compose definition, not image health checks.

## Reproduction

Run from `new_refactor_src`. Existing published profiles and their private `.env` files are used. Do not start Lite and Full simultaneously on their shared published frontend port.

```sh
docker compose -p froststream-phase0-lite -f Lite/docker-compose/docker-compose.yaml up -d --build
python3 tooling/installation/check-packaged-lite.py --output .phase0/results/packaged-lite.json
docker compose -p froststream-phase0-lite -f Lite/docker-compose/docker-compose.yaml restart lite
python3 tooling/installation/check-packaged-lite.py --output .phase0/results/packaged-lite-restart.json
docker compose -p froststream-phase0-lite -f Lite/docker-compose/docker-compose.yaml stop

docker compose -p froststream-phase0-full -f Full/docker-compose/docker-compose.yaml build
python3 tooling/installation/start-packaged.py Full --project froststream-phase0-full --output .phase0/results/packaged-full-startup.json
python3 tooling/installation/start-packaged.py Full --project froststream-phase0-full --verify-only --output .phase0/results/packaged-full-health.json

mkdir -p /tmp/frost-phase0-browser
pnpm add --dir /tmp/frost-phase0-browser playwright@1.64.0
/tmp/frost-phase0-browser/node_modules/.bin/playwright install chromium
node tooling/installation/check-authentik.mjs --playwright-module /tmp/frost-phase0-browser/node_modules/playwright/index.mjs --env-file Full/docker-compose/.env --output .phase0/results/packaged-full-auth.json
podman-compose --in-pod=false -p froststream-phase0-full -f Full/docker-compose/docker-compose.yaml stop
```

All Phase 0 stacks were stopped after verification; volumes and bind data were retained for inspection. Restarting a retained stack is not another empty-state install. Use a distinct project name and separate Full backup/bootstrap bind roots for a future truly fresh Full repetition.

## Second empty-state Full installation — 2026-10-11

`froststream-phase0-full-repeat` passed startup and final read-only verification: 15 running services, four healthy declared health checks, and four initializers exited zero. The existing images and profile credentials were reused; database, application, search, vault and session-key volumes were new. Before startup, the runner verified that the project had no containers or named volumes. Backup and vault-bootstrap bind roots were created separately under `.phase0/installations/froststream-phase0-full-repeat/`; actual PostgreSQL and vault mount inspection confirmed isolation from the first installation.

The real Authentik browser checkpoint also passed against this fresh database: two new users logged in through OIDC, retained sessions after reload, could not list/read/update each other's private playlists, and passed logout CSRF and revoked-cookie replay checks. Fixture identities/playlists were cleaned up. Reports are `.phase0/results/packaged-full-repeat-{startup,health,auth}.json`. A deliberate reuse attempt failed before startup, recorded in `packaged-full-repeat-reuse-rejected.json`. All 15 Phase 0 tooling tests pass, including state-reuse and path-traversal rejection.

Use a new project name for each fresh installation:

```sh
python3 tooling/installation/start-packaged.py Full --project froststream-phase0-full-repeat --fresh --output .phase0/results/packaged-full-repeat-startup.json
python3 tooling/installation/start-packaged.py Full --project froststream-phase0-full-repeat --verify-only --output .phase0/results/packaged-full-repeat-health.json
node tooling/installation/check-authentik.mjs --playwright-module /tmp/frost-phase0-browser/node_modules/playwright/index.mjs --env-file Full/docker-compose/.env --output .phase0/results/packaged-full-repeat-auth.json
podman-compose --in-pod=false -p froststream-phase0-full-repeat -f Full/docker-compose/docker-compose.yaml stop
```

`--fresh` refuses existing project containers, volumes or installation directories. Subsequent runner startup without `--fresh` automatically restores that project's isolated bind roots; it is a retained-state restart. Direct Compose startup must supply the same `FROSTSTREAM_BACKUP_ROOT` and `FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT` overrides. Stops retain all state. This repetition still uses the ordered Podman workaround and does not establish standard Compose compatibility or Full media behavior.

Still open: standard Full Compose startup, distributed Full media workflows, large-corpus indexing/latency/combined-workload measurements, and actual 8 GB reference-machine acceptance. The provisional budgets are unchanged.
