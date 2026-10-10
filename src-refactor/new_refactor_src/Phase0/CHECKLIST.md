# Phase 0 checkpoint

Status: baseline implementation and local regression checkpoint delivered; full Phase 0 acceptance remains open for the explicitly unverified gates below. No architecture behavior cutover is claimed.

- [x] Self-contained application/frontend/tests copied into new_refactor_src; reference is read-only.
- [x] Requested logical layout, stable assembly identities and source relocation map.
- [x] Service/dependency/API inventory and documented existing boundary exceptions.
- [x] Architecture drift guard with negative tests for cycles/external/missing inputs.
- [x] Private fresh-install environment generator and Lite/Full Compose publication.
- [x] Both generated profiles have valid local build/config paths.
- [x] Deterministic fresh Lite fixtures pass download/import/process/playback/search and local Admin access twice.
- [x] Unit and controlled Full authentication/OpenFGA characterization pass.
- [x] Versioned parser corpus and deterministic 10,000/100,000/250,000 record fixture generator with byte counts/hashes.
- [x] Reconciled testing strategy in working-tree TESTS_PLAN.MD: reject Full single-user from Phase 2; Lucene fixture cutover in Phase 5; durability/rebuild/resource gates assigned.
- [x] Development-machine process-tree measurement captured with hardware and limitations.
- [x] Packaged Lite startup from two separate empty-state installations and backend restart: frontend/API capabilities, SQLite backup provider and local Admin identity.
- [x] Packaged Full startup on an initially empty engine using isolated project state and the documented Podman ordering workaround; all 19 service/initializer states verified.
- [x] Real Authentik browser OIDC login/session flow, two-user private-playlist isolation and logout/cookie revocation; passed twice with fresh identities against real Full services.
- [ ] Standard Full Compose startup without the Podman workaround, second empty-state Full install, and distributed Full download/import/playback.
- [ ] Index the large corpus and measure full/incremental indexing, warm/cold search, idle activity, playback and combined workloads.
- [ ] Measure on the specified 8 GB machine including container memory, FFmpeg children and filesystem cache; confirm acceptance thresholds against measurements.

Known defects/differences: see ARCHITECTURE.md and EVALUATION.md. Keep Phase 0 open until remaining measurements, deployment and Full media gates pass. Do not infer acceptance from generated Compose, directory layout, mocked responses, or the development-machine resource sample.

Packaged verification commands, evidence scope and engine limitations are recorded in PACKAGED.md. The real authentication gate above covers private playlists; it does not claim private-note/search parity or Full media execution.
