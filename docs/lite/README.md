# FrostStream Lite phase evidence

Phase 1 artifacts:

- [`phase-01-inventory.md`](phase-01-inventory.md): extraction, transport, lifecycle, persistence, route, and backup inventory.
- [`phase-01-baseline.md`](phase-01-baseline.md): observed versions, build/test results, prerequisites, resource-measurement procedure, and gate status.
- [`phase-01-smoke-test.md`](phase-01-smoke-test.md): reproducible Full behavioral smoke test.

Phase 2 artifact:

- [`phase-02-compose-publishing.md`](phase-02-compose-publishing.md): pinned Aspire toolchain, isolated four-profile fixture, native publish/prepare behavior, graph assertions, and verification evidence.

Phase 3 artifact:

- [`phase-03-production-profiles.md`](phase-03-production-profiles.md): production profile selection, installation-scoped configuration, preserved resolved inputs, native Full output, and verification evidence.

Phase 4 artifact:

- [`phase-04-explicit-initialization.md`](phase-04-explicit-initialization.md): versioned application initialization, runtime compatibility checks, transitional Full wiring, disposable-state evidence, and the remaining pgBackRest runtime check.

Phase 5 artifact:

- [`phase-05-full-lifecycle.md`](phase-05-full-lifecycle.md): independent Full init/runtime bundles, OpenBao restart behavior, complete application smoke, persistence, and resource evidence.

Phase 6 artifact:

- [`phase-06-application-operations.md`](phase-06-application-operations.md): reusable application registration, typed direct/Full note adapters, current-owner abstraction, migration matrix, parity tests, and authenticated Full smoke evidence.

Phase 7 artifact:

- [`phase-07-durable-workflows.md`](phase-07-durable-workflows.md): durable ingress/executor contracts, Full acknowledgment boundary, recovery matrix, bounded progress snapshots, reconstruction tests, and authenticated Full workflow evidence.

Phase 8 artifact:

- [`phase-08-merged-lite-host.md`](phase-08-merged-lite-host.md): merged fixed-owner Lite API host, constrained route surface, composition validation, and HTTP smoke evidence.

Phase 9 artifact:

- [`phase-09-encrypted-local-secrets.md`](phase-09-encrypted-local-secrets.md): encrypted local secret persistence, recovery behavior, credential hydration, backup manifest, and restart evidence.

Phase 10 artifact:

- [`phase-10-exclusive-local-execution.md`](phase-10-exclusive-local-execution.md): PostgreSQL ownership lease, durable local ledger and dispatcher, bounded snapshot-first SSE, crash recovery, lock-loss, and controlled-executor evidence.

Evidence containing credentials, recovery material, dumps, cookies, or downloaded media must remain outside source control. Commit only redacted summaries.
