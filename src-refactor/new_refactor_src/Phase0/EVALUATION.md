# Implementation evaluation — 2026-10-10

The Phase 0 working baseline is implemented and locally verified. The continuation also verifies packaged Lite startup/restart, Full startup with a documented Podman workaround, and real Authentik browser sessions/private-playlist isolation twice. Phase 0's complete exit gate is **not yet satisfied**: packaged Full media workflows, standard Full Compose startup/repeated empty-state installation and the measured 8 GB performance acceptance remain open. See PACKAGED.md and CHECKLIST.md.

All new work is under `src-refactor/new_refactor_src/`. The supplied `src-refactor/reference_src/` was read only. The user's pre-existing changes to REARCH_PLAN.MD were preserved. No old-tree libraries, project references or linked compilation inputs are required. No existing deployment/volume was reset. The continuation started isolated `froststream-phase0-*` packaged projects on an initially empty engine; their volumes were retained.

The mechanical copy establishes a runnable implementation where the next phases will work. It is larger than a documentation-only Phase 0 change because the destination was empty and the old source is scheduled for deletion. Existing application behavior, namespaces and assembly identities remain intact. Target directories reserved for later extraction do not pretend to contain completed libraries/hosts.

Review found and corrected two classes of issues:

- Existing test code still used Quartz 3 logging, existence, state and misfire APIs despite the pinned Quartz 4.3.0 dependency. Updated only test adapters/assertions using the installed Quartz XML API documentation. Production scheduling was preserved.
- Relocation required project/linked-file references, Docker COPY paths, AppHost paths, Compose contexts, frontend branding binds and default storage roots to point inside the working tree. Generated profile validation caught a remaining BackupService context escaping the tree; corrected it and regenerated Full.

The architecture check deliberately snapshots the existing graph and reports its exceptions. It enforces containment, acyclicity and reviewed drift now; it is not the final strict provider-isolation checker. Five negative/positive tooling tests prove the guard rejects cycles, external references, missing references and external source links.

| Verification | Result |
| --- | --- |
| Entire working solution build, including both AppHosts | Pass; 0 errors, two ASPIRE010 CLI-bundle warnings inherited from the environment configuration. |
| Unit checkpoint | 585 passed, 0 failed/skipped. Includes ten-case parser corpus characterization. |
| Real Lite download/import/process/playback/search/restart/crash suite | 3 passed, 0 failed/skipped; passed again from fresh fixture state. |
| Controlled Full authentication/OpenFGA checkpoint | 17 passed, 0 failed/skipped; passed again from fresh fixture state. Real OpenFGA, controlled JWT identities. |
| Packaged Full real Authentik checkpoint | Passed twice with fresh identities: OIDC login/session, bidirectional private-playlist isolation, CSRF/logout and revoked-cookie replay. All 19 service/initializer states verified using the documented Podman workaround. |
| Packaged Lite smoke | Fresh frontend/API/local Admin checks and backend restart passed; see PACKAGED.md for scope and reproduction. |
| Existing frontend access and backup suites | Both passed. Node reports one test-file result per command; no browser-suite count is inferred. |
| Frontend check and production build | Pass; check reports 0 errors/warnings. |
| Source graph/API containment and drift | Pass: 22 projects, central versions, route attributes from 34 API source files. |
| Generated Compose profiles | Lite 4 services; Full 19 services. Both publish and pass source/build path validation. Packaged startup evidence and the standard Full Compose provider failure are recorded in PACKAGED.md. |
| Corpus generator | 10,000 media / 100,000 captions / 250,000 comments; byte sizes and SHA-256 hashes recorded in corpus-manifest.json. Not seeded/indexed into an engine. |
| Resource sampler failure/timeout checks | Nonzero child exit propagated (3); timed command fails (124), terminates its own process group and writes evidence. |

Development measurement: the second Lite characterization run took 124.96 seconds including runner overhead; peak sampled aggregate process-tree RSS was 1,010,909,184 bytes (964.08 MiB). The machine exposes 16 logical CPUs and 32,708,534,272 bytes of RAM (~30.46 GiB). Sample includes descendant FFmpeg/crash-host processes when observed. RSS can double-count shared pages and miss short-lived children; Typesense/container memory is excluded. System cache/available memory was sampled but is not attributed to FrostStream. This small generated-media run proves neither full-corpus capacity, search latency, idle CPU nor improvement over the old architecture. See development-measurement.json; raw samples/reports remain in ignored `.phase0/results/`.

Acceptance budgets in acceptance.json are explicitly provisional, with measuredLaptopBaseline=null. They are not fabricated measured thresholds. Before Phase 5 acceptance, index the fixed corpus and measure warm/cold latency, startup, idle activity, incremental/full indexing and sustained combined workloads on an actual 8 GB reference machine, including frontend/POT/optional ClickHouse and filesystem cache.

Known baseline product/packaging issues remain named rather than frozen into contracts: Lite contains Full SDKs and executable references; Lite still uses Typesense (profile 30.2, runtime fixture 30.1); Full single-user flags still exist; frontend publishing uses all interfaces; search rebuild/queue/index durability need their assigned phases; encoding Stop/Retry UI gaps remain in TESTS_PLAN.MD. Fresh-install fixtures force Full multi-user mode but do not implement Phase 2's obsolete-flag rejection early.

A repository-wide `git diff --check` reports trailing whitespace in the user's pre-existing REARCH_PLAN.MD edit. That document was not modified as part of this implementation.
