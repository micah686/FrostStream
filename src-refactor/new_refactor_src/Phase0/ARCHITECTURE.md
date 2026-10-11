# Phase 0 architecture and boundary baseline

`dependency-baseline.json` now captures 27 project declarations after Phase 1 (22 in the initial Phase 0 inventory), direct references, package names, pinned central package versions, and route attributes from 34 API source files. `lite-resolved-dependencies.json` captures the historical Phase 0 Lite transitive graph. Route attributes are source evidence, not a claim that every route was requested. `verify.py` fails on external/missing project references, external/missing compile links, cycles, forbidden executable boundaries, or drift from the reviewed source graph/API snapshot.

| Current process | Responsibilities and dependencies |
| --- | --- |
| Lite/Backend | Hosts WebAPI, DataBridge, Worker, MediaProcessor and Scheduler; SQLite application/workflow/transport state, encrypted local secrets, filesystem storage; Typesense remains external. |
| Full WebAPI (`Full/Hosts/WebAPI`, composing `Shared/Modules/Api`) | HTTP/BFF, browser events, Authentik/OpenFGA; requests DataBridge/Worker/MediaProcessor over NATS. |
| Full DataBridge | EF/PostgreSQL authoritative records, orchestration, workflows, Typesense projection, secrets via OpenBao. |
| Full Worker | Acquisition/import, media tools, storage; receives durable work over NATS. |
| Full MediaProcessor | FFmpeg encoding and derived artifacts; independent durable consumers. |
| Full Scheduler | Quartz triggers, dispatches application operations. |
| Full BackupService | Separate executable, PostgreSQL/pgBackRest and OpenBao recovery, restore console. |
| Frontend | One Svelte application served by Caddy; application API through backend proxy. |

Generated Lite profile: backend, frontend, POT provider, Typesense (four services, live chat off). Generated Full profile: 19 services including initialization tasks, Authentik server/worker, OpenFGA, NATS, PostgreSQL, OpenBao, Typesense, BackupService, POT, frontend and five separate application services. Live chat and development tools are disabled in Phase 0 fixtures.

Target rules are those in REARCH_PLAN.MD section 3: hosts select infrastructure; modules depend on Contracts/Core and shared Persistence; infrastructure/integrations implement shared ports; no executable references an executable; provider-neutral contracts/persistence contain no vendor types; Full.Security is used only by Full WebAPI. Final Lite excludes NATS, Npgsql/PostgreSQL, Typesense, VaultSharp, Authentik and OpenFGA implementations. The isolated Typesense exception exists only until Phase 5.

Phase 1 resolved the executable-module and competing-appsettings violations. The five modules are libraries; Full shells live under `Full/Hosts`, and Lite's removal targets are gone. See `../Phase1/README.md` for verification.

Known remaining violations are deliberately retained for characterization:

- Shared/Core still owns EF entities, storage/security/provider code; DataBridge owns both persistence providers (Phase 2).
- Lite's resolved packages include NATS, Npgsql, Typesense, VaultSharp/OpenFGA dependencies (Phase 2/5); absence of runtime connections does not prove dependency isolation.
- Module registration chooses infrastructure; schema initialization and scheduler manipulation remain order sensitive (Phase 2).
- Search/parser consumers use Typesense filter/result types and rebuilds can clear active collections (Phase 4).
- SQLite queue polling/retention and durable projection intent require Phase 3.
- Full's former single-user selectors still exist (Phase 2). Historical tests are not a future compatibility requirement.

A change to the graph must be reviewed against these rules before explicitly running `python3 Phase0/verify.py --capture`. The captured exceptions do not authorize new violations or constitute the strict Phase 7 boundary checker. Resolved package inventory is historical evidence; recapture from a clean restore when dependencies intentionally change.
