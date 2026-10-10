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
- [ ] Boot both candidate packaged profiles from empty state on a dedicated disposable engine; repeat Full download/import/playback through distributed services.
- [ ] Real Authentik browser login/session flow and real Full private-data isolation; controlled JWT coverage does not satisfy this.
- [ ] Index the large corpus and measure full/incremental indexing, warm/cold search, idle activity, playback and combined workloads.
- [ ] Measure on the specified 8 GB machine including container memory, FFmpeg children and filesystem cache; confirm acceptance thresholds against measurements.

Known defects/differences: see ARCHITECTURE.md and EVALUATION.md. Keep Phase 0 open until remaining measurements and real deployment/authentication gates pass. Do not infer acceptance from generated Compose, directory layout, mocked responses, or the development-machine resource sample.
