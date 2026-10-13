# Final architecture checks

`verify-boundaries.py` enforces final composition ownership, transitive edition isolation, lower-layer direction, implementation-independent contracts, provider-neutral shared source, removed migration runners and the restored Lite package graph. `tooling/architecture/verify.py` separately enforces acyclic, contained project references, library/host separation, host-owned settings and a reviewed dependency/API inventory. `tooling/recovery/verify-baselines.py` pins the immutable fresh-install resources.

`test_boundaries.py`, `tooling/architecture/test_provider_boundaries.py` and `tooling/architecture/test_verify.py` introduce deliberate violations and assert rejection. A new violation fails the `Refactor boundaries and installations` GitHub workflow. Inventory updates require explicit review; CI never captures a new baseline automatically.

The workflow builds the solution, enables real PostgreSQL/Typesense contracts, runs all backend integration tests and frontend suites, then builds and probes fresh Lite/Full packages with live chat disabled and enabled. Each installation uses a new private output directory and random Compose project name. Verification removes its containers while retaining volumes; it never resets shared state or uploads deployment secrets.

```sh
python3 tooling/installation/verify-install.py Lite
python3 tooling/installation/verify-install.py Full
python3 tooling/installation/verify-install.py Lite --live-chat
python3 tooling/installation/verify-install.py Full --live-chat
# Standalone Compose against a Podman API socket:
python3 tooling/installation/verify-install.py Full --compose-executable /path/to/docker-compose
```

The cleanup removes the PostgreSQL historical migration code/SDK/runner, SQLite opt-in selector, backup/Typesense configuration aliases, legacy OpenBao bootstrap adoption, Full single-user branches, compatibility auth helper, stale assembly naming and temporary Typesense documentation. Storage HTTP fixtures now use explicit Full identity configuration, controlled signed JWTs and real OpenFGA. Lite administrator seeding is a synchronous initialization step with no Full/no-op background-service mode. Legitimate idempotent module markers and frozen schema capture metadata remain.

Resource acceptance on the specified physical 8 GB reference machine, real Authentik browser login and external HTTPS certification remain separate acceptance work. Existing larger-machine search measurements and controlled identity tests must not be presented as that evidence.

## Verification on 2026-10-11

The solution builds with zero errors and two existing Aspire CLI bundle warnings. All 629 unit tests (real PostgreSQL/Typesense enabled), 88 integration tests and 27 architecture violation tests pass. Frontend access (5) and backup (2) tests, type checks (zero diagnostics) and production build pass. Fresh Lite/Full packages pass with live chat off/on: 3/19/4/20 services respectively. Frozen schema checksums and the reviewed API inventory pass unchanged; the dependency inventory removes only the three FluentMigrator package pins/references.

The final backend runs were sequential with four concurrent tests per suite. An earlier run alongside container builds hit a search RPC timeout and a rootless Podman port collision; the complete final reruns passed. CI uses the same concurrency bound. [Machine-readable evidence](../verification/evidence/phase-7-verification-results.json) records results and scope. Recovery procedures were validated in Phase 6; this phase validates fresh installation and boundary cleanup. Hosted GitHub Actions execution awaits the next push/PR.
