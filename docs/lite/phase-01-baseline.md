# Phase 1 baseline evidence

Status: Phase 1 evidence capture complete; Full runtime smoke is blocked by the recorded Podman Compose incompatibility. Captured 2026-09-10 at commit `9ebc0ac` on branch `feature/lite-implementation`.

## Toolchain and pinned service versions

| Component | Observed/pinned version | Source |
| --- | --- | --- |
| .NET SDK/runtime | SDK 10.0.100; .NET/ASP.NET Core runtime 10.0.0 | `global.json`, `dotnet --info` |
| Aspire AppHost SDK | 13.2.2 | `src/App/AppHost/AppHost.csproj` |
| Aspire AppHost SDK/hosting packages/CLI | SDK 13.2.2; centrally managed packages 13.5.3; CLI 13.4.6 | AppHost project, `src/Directory.Packages.props`, and `aspire --version`. These mismatches are a Phase 2 decision. |
| Node/pnpm | Node 26.8.1; pnpm 10.28.1 | local executables; frontend pins pnpm 10.28.1 and requires Node >=22 |
| PostgreSQL | 18.3 | generated Compose and BackupService runtime base image |
| NATS | 2.14 | generated Compose |
| OpenBao | 2.5.5 | AppHost hardening defaults/generated Compose |
| Typesense | 30.2 | AppHost hardening defaults/generated Compose |
| Authentik | 2026.5.3 | AppHost hardening defaults/generated Compose |
| OpenFGA | v1.18.0 | AppHost hardening defaults/generated Compose |
| ClickHouse | 25.8 | AppHost default/generated Compose |
| bgutil POT provider | 1.3.1 | generated Compose |
| ffmpeg/ffprobe | Host 9.0.1; built MediaProcessor image ffmpeg 6.1.1-3ubuntu5 | Local executable and direct execution of the built image. |
| yt-dlp on host | 2026.08.19 | local executable. Worker downloads managed binaries at startup, so runtime verification must record that version too. |
| pgBackRest | Not installed on host; built BackupService image 2.59.1 | Direct execution of the built image. |

## Host baseline

- Linux `7.2.4-1-cachyos`, x86_64.
- AMD Ryzen 7 7800X3D, 8 cores/16 threads.
- 30 GiB RAM and 30 GiB swap. At capture time, 11 GiB RAM was used and 18 GiB available; this is host context, not FrostStream idle usage.
- Docker commands route through Podman Compose 1.6.0. Escalated container access works, but its completed-dependency behavior blocks the generated Full topology as described below.

## Static deployment topology

The committed normal Compose artifact contains 20 services: NATS, PostgreSQL, `backup-init`, `postgres-init`, OpenBao, `openbao-bootstrap`, Typesense, Authentik server/worker, `openfga-migrate`, OpenFGA, POT provider, BackupService, ClickHouse, DataBridge, WebAPI, Worker, MediaProcessor, Scheduler, and Frontend. It also defines seven persistent volumes plus the Aspire network. This confirms that one-shot jobs currently appear in the ordinary artifact.

Static topology is not a resource measurement. Idle memory, readiness time, live container count, and representative workload measurements remain unverified until the runtime procedure below can access a container engine.

## Build and test results

| Check | Result | Classification |
| --- | --- | --- |
| `dotnet restore src/App/FrostStream.slnx` | Passed | Refreshed stale assets. Central management plus the direct Shared reference resolve `SSH.NET` 2026.0.0. No package edit was required. |
| `dotnet build src/App/FrostStream.slnx --no-restore` | Passed | 0 errors; one `ASPIRE010` warning because `AspireUseCliBundle=false`. |
| Unit test command | 457 passed, 5 failed, 0 skipped (462 total) | Tests execute after restore. Existing failures: access-policy create expected Accepted but received BadRequest; WorkersController lacks endpoint description; note deletion expected NotFound but received NoContent; yt-dlp auth error text mismatch; missing comment-author fallback expected `unknown` but received empty. |
| Integration test command | 84 assertions passed, 0 failed, then process exited 134 | Teardown throws concurrent collection-modification exceptions in `SubscriptionBackgroundService.StopAsync` and `BackgroundJobHub.StopAsync`. Treat the command as failed despite the passing test summary. |
| `pnpm run check` | Passed | 0 errors and 0 warnings. |
| `pnpm run build` | Passed | Static adapter wrote the site to `src/App/Frontend/build`. |

The SSH.NET advisory is resolved in the current package graph. `src/Directory.Packages.props` pins 2026.0.0 and `Shared.csproj` directly references it to override `FluentStorage.SFTP`; the earlier NU1903 result came from running with stale `obj/project.assets.json` files and `--no-restore`. `dotnet list ... --include-transitive` now reports both requested and resolved SSH.NET as 2026.0.0. Phase 1 did not suppress NuGet auditing or weaken warnings-as-errors.

The five unit failures and integration teardown crash are baseline defects outside the SSH.NET correction. They are recorded rather than hidden by changing tests during this inventory phase.

## Full startup attempt and resource result

The committed Compose graph validates with `docker compose ... config --quiet`, and all application images build. Starting it with the committed `.env` fails as expected because every parameter value is empty; PostgreSQL reports a missing superuser password and Typesense reports a missing API key. This demonstrates that `.env` is an unfilled contract, not a runnable deployment file.

Starting with populated `example.env` reaches healthy PostgreSQL and running NATS, Typesense, ClickHouse, POT, OpenBao, and Worker. Podman Compose then blocks at the first successful one-shot dependency: `backup-init` exits 0, but Podman reports its state as “improper” when starting `postgres-init`. A direct start of `postgres-init` fails on the same stored dependency. Downstream Authentik, OpenFGA, BackupService, DataBridge, WebAPI, MediaProcessor, Scheduler, and Frontend therefore remain created rather than running. This is a Docker-Compose-versus-Podman-Compose compatibility failure in the existing generated lifecycle graph, not a credential failure.

The failed readiness attempt was stopped after 120 seconds. A resource snapshot of the seven running services measured approximately 1.35 GB total memory: NATS 18.43 MB, OpenBao 32.67 MB, Typesense 265.5 MB, POT provider 66.01 MB, ClickHouse 767.6 MB, PostgreSQL 30.76 MB, and Worker 172.2 MB. These are partial-start diagnostic values, not the Full idle baseline and not suitable for the Phase 21 Full/Lite comparison.

Because the application tier cannot start on the available engine without altering the generated artifact, login/download/search/playback/backup workload measurements cannot be honestly produced here. The exact smoke procedure remains ready for Docker Compose or for revalidation after Phase 5 corrects lifecycle portability.

## Test prerequisites

- .NET 10 SDK selected by `global.json`, restored NuGet packages, and an audit-clean dependency graph.
- Node >=22 and pnpm 10.28.1 for frontend checks.
- Docker Compose with working `service_completed_successfully` dependency handling for the current generated Full graph, or the lifecycle portability fix owned by Phase 5; access to published ports on localhost.
- Aspire CLI for publication tests. In this sandbox it reports its version but cannot write its normal log under `/home/micah/.aspire`; set a writable CLI home/log location when needed or run outside the restricted sandbox.
- Sufficient writable storage for PostgreSQL, ClickHouse, Typesense, OpenBao, media, and backup fixtures.
- Synthetic Authentik/OpenBao/database credentials and an ignored deployment env; never reuse production secrets.
- A small stable yt-dlp-supported test source or a controlled local provider, a media fixture for import/playback, optional YouTube/POT fixture, and a LAN casting device for the hardware check.

## Repeatable backend baseline commands

From the repository root:

```bash
dotnet restore src/App/FrostStream.slnx
dotnet build src/App/FrostStream.slnx --no-restore
dotnet run --project Tests/UnitTests/UnitTests.csproj --no-restore -- --no-ansi --disable-logo
dotnet run --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj --no-restore -- --no-ansi --disable-logo
```

From `src/App/Frontend`:

```bash
pnpm install --frozen-lockfile
pnpm run check
pnpm run build
```

## Full resource baseline procedure

Use a disposable installation and record all resolved versions without printing secrets.

1. Record commit, kernel, CPU, total RAM, .NET/Aspire/Node/pnpm and container-engine versions.
2. Prepare a sanitized disposable Full env with fixed test credentials, `LIVE_CHAT_ENABLED=true`, a stable absolute media path, and a stable absolute backup path.
3. Validate the Compose file, build images, and capture image IDs/sizes. Start from empty disposable volumes and record wall-clock time from `up -d` until every required application/infrastructure health check reports ready.
4. Record live service names, health, restart counts, and container count. After five quiet minutes, sample container memory/CPU three times at 30-second intervals and report median per service and total.
5. Execute the smoke procedure in `phase-01-smoke-test.md`. Record source duration/size, download wall time, peak/median CPU and memory, resulting media size, search latency, and playback start latency.
6. Run a backup and record duration/repository delta. Stop and recreate runtime containers without deleting volumes; confirm login, search, playback, and backup listing remain available.
7. Save redacted commands/results under `docs/lite/evidence/phase-01/<date>/`. Do not commit `.env`, OpenBao bootstrap material, cookies, tokens, database dumps, or downloaded copyrighted media.

Suggested measurements, adapted to the available Docker-compatible engine:

```bash
/usr/bin/time -f 'ready_wall=%e' docker compose --env-file <env> -f <compose> up -d --build
docker compose --env-file <env> -f <compose> ps
docker stats --no-stream
docker compose --env-file <env> -f <compose> images
```

The readiness timer must poll actual health endpoints/health states; `docker compose up -d` returning is not readiness. Phase 21 repeats the exact workload on Full and Lite on the same host.

## Gate status

- Implementation inventory with file evidence: complete.
- Reproducible commands and test prerequisites: complete.
- Build/frontend/test baseline: complete. SSH.NET resolves to 2026.0.0; remaining test failures are enumerated above.
- Full startup/resource attempt: complete and reproducible. The existing graph fails under Podman Compose at a successful one-shot dependency; the partial resource snapshot is recorded.
- Successful Full application smoke/workload measurement: blocked until run with compatible Docker Compose or after Phase 5's lifecycle work. It must be added before Phase 21 uses this as a comparison baseline.

Phase 1's requested investigation and evidence capture are complete, including the failed-start evidence. Its successful-runtime verification remains blocked and is not represented as passing. The baseline containers and volumes created by this run are intentionally left in place for review; no volumes were deleted.
