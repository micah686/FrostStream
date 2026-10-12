# FrostStream architecture

Run commands from this directory. The solution is self-contained; projects and linked source never depend on the original repository `src/` tree.

Shared contracts, core, persistence and application modules implement product behavior once. Lite composes those modules in one executable with SQLite, durable local messaging, encrypted local secrets, local administrator identity and embedded Lucene. Full composes separate executable hosts with PostgreSQL, NATS, OpenBao, Authentik/OpenFGA and Typesense. ClickHouse is optional in both compositions. Shared code references provider-neutral contracts; executable hosts select edition adapters.

| Directory | Responsibility |
| --- | --- |
| `Shared/Contracts` | Implementation-independent application contracts |
| `Shared/Core` | Common domain, messaging, identity and search abstractions |
| `Shared/Persistence` | EF model, repositories, query catalog and schema resources |
| `Shared/Modules` | Reusable API, DataBridge, Worker, MediaProcessor and Scheduler libraries |
| `Lite/Infrastructure` | SQLite, local transport/secrets/identity/recovery and Lucene adapters |
| `Lite/Backend` | Single-process composition and ordered lifecycle |
| `Full/Infrastructure`, `Full/Security` | Full transport/secrets, PostgreSQL and identity adapters |
| `Full/Hosts` | Thin executable composition roots |
| `Shared/Integrations/Typesense` | Full DataBridge's isolated search adapter |
| `Frontend` | One capability-driven UI for both editions |

`Deployment:Mode` is resolved at startup. Lite owns SQLite and Full owns PostgreSQL; the former `Persistence:Sqlite:Enabled` opt-in is rejected. Full rejects former single-user switches, including false values. Full security belongs only to the WebAPI host. Typesense belongs only to Full DataBridge. Libraries cannot reference executable hosts, shared code cannot depend on edition implementations, and Lite's restored package graph cannot contain Full SDKs. Module registration markers enforce idempotent registration and are part of the lifecycle contract.

Prerequisites: .NET 10, Node 22 or newer, pnpm 10.28.1, Python 3, FFmpeg/FFprobe and a Docker-compatible engine. Package generation also needs Aspire CLI 13.4.6 and `python3 -m pip install -r Phase0/requirements.txt`. For rootless Podman, configure `DOCKER_HOST` for its API socket and `TESTCONTAINERS_RYUK_DISABLED=true`. Full installation checks require Docker Compose with working completed-job dependencies.

```sh
dotnet build FrostStream.slnx -m:1
python3 Phase0/verify.py
python3 Phase0/test_verify.py
python3 Phase2/test_boundaries.py
python3 Phase7/test_boundaries.py
python3 Phase7/verify-boundaries.py --require-restored
python3 Phase6/verify-baselines.py
dotnet run --no-build --project Tests/UnitTests/UnitTests.csproj -- --maximum-parallel-tests 4
dotnet run --no-build --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- --maximum-parallel-tests 4
cd Frontend
pnpm install --frozen-lockfile
pnpm test:access
pnpm test:backups
pnpm check
pnpm build
```

The backend suites exercise real local storage/workflows/search, NATS, PostgreSQL and OpenFGA. Set `FROSTSTREAM_TEST_POSTGRES` to a disposable administrator connection string and `FROSTSTREAM_TEST_TYPESENSE` to a disposable Typesense URL with key `phase4-test-key` to enable the extra cross-provider contracts. Full HTTP tests use controlled JWT signing with real OpenFGA; they do not certify a real Authentik browser login or an external HTTPS deployment.

See [installation and recovery](Phase6/README.md), [search durability and resource evidence](Phase5/README.md), [current testing strategy](TESTS_PLAN.MD) and [final boundary enforcement](Phase7/README.md). Phase 0–6 reports retain historical evidence; current operating instructions are the Phase 6 guide and this page. Hashed schema resources remain immutable, including inert historical capture metadata; no migration runner or old-database adoption path ships.
