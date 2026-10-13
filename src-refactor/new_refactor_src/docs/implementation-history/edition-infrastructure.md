# Phase 2 — Explicit edition infrastructure and identity

The edition hosts now own provider composition. `Shared/Core`, `Shared/Contracts`, shared feature modules, and `Shared/Persistence` have no NATS, PostgreSQL, SQLite, Typesense, or VaultSharp SDK dependency. `Shared/Persistence` retains the existing operation-specific SQL, EF mappings, mutation/retry rules, and storage contracts through narrow provider hooks.

| Library | Responsibility | Production users |
| --- | --- | --- |
| `Shared/Persistence` | Logical entities/models, SQL operations, transactions, workflow contracts | DataBridge and edition persistence adapters |
| `Lite/Infrastructure` | SQLite schema/workflows/inbox, local dispatcher/secrets/snapshots, Admin identity | Lite backend |
| `Full/Infrastructure` | NATS transport/topologies, OpenBao, remote backups | Full hosts, with explicit registration by role |
| `Full/Infrastructure/Postgres` | PostgreSQL mappings/migrations/workflow store | Full DataBridge |
| `Full/Security` | Authentik/BFF/JWT, NATS session tickets, OpenFGA, browser and access-control controllers | Full WebAPI only |
| `Shared/Integrations/Typesense` | Existing search implementation and projection consumers | Lite and Full DataBridge |

Typesense is the single temporary Lite infrastructure exception. Phase 4 completes the search contract; Phase 5 replaces this adapter in Lite with embedded search. This phase preserves existing search and workflow behavior rather than introducing a new query engine or queue algorithm.

## Composition and identity

Full hosts install messaging explicitly. WebAPI, DataBridge, and Worker install the secret store; WebAPI and Scheduler install the remote backup client. Only DataBridge registers PostgreSQL; only WebAPI references Full security. BackupService retains its independent recovery startup and outbound-only messaging behavior.

Lite selects `AddLitePersistence`, `AddLiteMessaging`, `AddLiteSecretStore`, `AddLiteBackupClient`, `AddLiteStagedStorage` and `AddLiteIdentity` in its composition root. Database-only initialization installs neither messaging nor secrets/backups; the runtime registrations resolve the selected database services when needed.

Full requires Authentik settings and an HTTP(S) OpenFGA endpoint. Disabling OpenFGA auto-provisioning requires explicit store/model IDs. `SINGLE_USER_MODE`, `Auth:SingleUserMode`, `AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION`, and `Auth:AllowSingleUserModeInProduction` are rejected in Full even when set to `false`. Remove them from existing development fixtures before publishing. Full AppHost no longer emits those flags or omits identity resources.

Lite registers local Admin authentication and authorization explicitly. It loads no Full security assembly and exposes no access-control management controller. Shared feature routes remain in the API module; browser login/logout and Full-only account management are supplied by edition application parts. Endpoint catalog checks cover both shared and Full controller assemblies.

## Initialization and readiness

`InitializeApplicationAsync` runs lazily resolved, named stages before a host starts its consumers:

1. Application schema (order 10; optional live-chat schema at 15).
2. Workflow schema (20).
3. Local Admin identity in Lite (30).
4. Interrupted-download reconciliation and its generation fence (40).
5. Existing Typesense startup/bootstrap behavior (50).

Lite then starts its hosted services. `IHostedLifecycleService.StartedAsync` awaits **all** application handler registration tasks, releases durable execution, hydrates and starts scheduling, and finally marks readiness. It does not depend on service descriptor position, remove/reinsert hosted services, or initialize schema in a constructor. Shutdown clears readiness before draining hosted services and shutting down Quartz. New hosts repeat reconciliation against retained state. The old synchronous `InitializeDataBridge` helper remains for schema characterization tests; production hosts use the explicit stages.

The PostgreSQL workflow adapter preserves Cleipnir's `flows` table prefix and `cleipnir,public` search path. SQLite baseline resource names and persisted value encodings are preserved. Immutable provider metadata is registered by each adapter, while connections and services remain owned by their host.

## Verification

```bash
python3 tooling/architecture/verify.py
python3 tooling/architecture/verify-final-boundaries.py --require-restored
python3 -m unittest discover -s Phase2 -p 'test_*.py'
bash tooling/installation/run-checkpoint.sh unit
bash tooling/installation/run-checkpoint.sh lite
bash tooling/installation/run-checkpoint.sh full
python3 tooling/architecture/check-publish-all.py
```

`verify-provider-boundaries.py` checks project closure, SDK imports, Full-security ownership, and Lite's actual restored transitive graph. The publish check inspects six fresh published outputs, dependency manifests, module DLLs, and host-owned settings. Outputs and private fixtures belong under ignored `.artifacts/`; regression artifacts remain under `.artifacts/test-results/phase2-*`.

See `../verification/evidence/phase-2-verification-results.json` for completed checks. Phase 0's outstanding standard Full Compose, large-corpus, distributed media, and 8 GB hardware gates remain separate; Phase 2 verification does not claim those benchmarks are complete.
