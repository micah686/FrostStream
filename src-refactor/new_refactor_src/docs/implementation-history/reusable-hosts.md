# Phase 1 — reusable libraries and thin hosts

The five projects under `Shared/Modules` are class libraries. Their assembly names and application namespaces remain unchanged, including embedded schemas and the OpenFGA model. Each Full executable now owns its entry point, settings, launch profile and Dockerfile under `Full/Hosts`. Lite still composes all five libraries in one process.

| Executable project | Entry assembly | Reusable module |
| --- | --- | --- |
| `Full/Hosts/WebAPI/WebAPIHost.csproj` | `FrostStream.Full.WebAPI` | `WebAPI` |
| `Full/Hosts/DataBridge/DataBridgeHost.csproj` | `FrostStream.Full.DataBridge` | `DataBridge` |
| `Full/Hosts/Worker/WorkerHost.csproj` | `FrostStream.Full.Worker` | `Worker` |
| `Full/Hosts/MediaProcessor/MediaProcessorHost.csproj` | `FrostStream.Full.MediaProcessor` | `MediaProcessor` |
| `Full/Hosts/Scheduler/SchedulerHost.csproj` | `FrostStream.Full.Scheduler` | `Scheduler` |
| `Lite/Backend/Lite.csproj` | `Lite` | All five |

Hosts create and run the application, select module HTTP mappings, and map process health endpoints. Reusable API middleware/controllers and the scheduler dashboard remain module extensions for host composition. Worker registration no longer replaces its embedding host's lifetime; standard .NET host builders provide console signal handling. DataBridge's host retains its initialization before `RunAsync`. Provider selection and initialization ordering inside modules remain Phase 2 work.

Full AppHost targets the new executables, with root build contexts and relocated Dockerfiles. Both AppHosts explicitly use metadata-only project references (`ReferenceOutputAssembly=false`); executable assemblies do not enter their runtime dependency graph. Test executables deliberately reference hosts to exercise composition. Production hosts and reusable libraries cannot reference executable projects.

The Lite appsettings build/publish removal targets are gone. Modules contain no appsettings or entry points. Full host settings retain their previous content. Lite previously suppressed all module settings and continues to use its host defaults/environment; its publish output has no module appsettings. `check-publish-all.py` publishes all six composition roots into fresh output, checks exact host-owned settings, required module DLLs, and the absence of module executable runtime configs.

HTTP integration factories now target `FrostStream.Full.WebAPI.Program`. Controller/endpoint unit tests locate the reusable API assembly through `WebAPIModule`. The graph guard additionally rejects executable modules, runtime references to executables, module entry points/settings, appsettings-removal targets and runtime AppHost references. Its reviewed snapshot in `tooling/architecture/dependency-baseline.json` now contains 27 projects; historical recorded measurements remain historical.

## Verification commands

Run from `new_refactor_src`:

```sh
dotnet build FrostStream.slnx --nologo -m:1
python3 -m unittest discover -s Phase0 -p 'test_*.py'
python3 tooling/architecture/check-publish-all.py
bash tooling/installation/run-checkpoint.sh unit
DOCKER_HOST=unix:///run/user/1000/podman/podman.sock TESTCONTAINERS_RYUK_DISABLED=true bash tooling/installation/run-checkpoint.sh lite
DOCKER_HOST=unix:///run/user/1000/podman/podman.sock TESTCONTAINERS_RYUK_DISABLED=true bash tooling/installation/run-checkpoint.sh full
dotnet Tests/IntegrationTests/IntegrationTests/bin/Debug/net10.0/IntegrationTests.dll --treenode-filter '/*/IntegrationTests.WebApiHttp/*/*'
python3 tooling/installation/check-compose.py
```

Publish verification refuses an existing output directory so stale files cannot pass; use `--output .phase1/publish-repeat` for a new run. Engine paths in the commands above describe this machine's rootless Podman setup. The existing Phase 0 fixture preparation, profile publication and packaged browser check also apply to these hosts. Fresh package projects for this phase are `froststream-phase0-full-phase1` and `froststream-phase0-lite-phase1`; the runner's required prefix preserves its disposable-project guard.

Phase 0's unresolved standard Compose provider failure, distributed Full media characterization and reference-hardware/performance gates remain open. A Phase 1 host extraction does not supply those missing measurements or claim new Full media coverage.

## Results — 2026-10-11

| Check | Result |
| --- | --- |
| Complete solution, including both AppHosts | Passed, zero errors; two inherited ASPIRE010 environment warnings |
| Unit suite | 585 passed; one earlier readiness timeout during concurrent builds passed in isolation and in the final full run |
| Real Lite download/import/playback/search, restart and crash recovery | 3 passed |
| Full controlled authentication/OpenFGA | 17 passed |
| HTTP controllers through the extracted Full WebAPI host | 6 passed |
| Architecture/tooling tests | 20 passed |
| Fresh publish outputs | All six composition roots passed |
| Compose profile publication and source paths | Lite and Full passed |
| Container builds | Combined Lite and all five new Full host images passed |
| Fresh packaged Lite | Standard Compose startup, frontend/API/capabilities/local Admin passed |
| Fresh packaged Full | All 19 service states and real Authentik two-user login/private playlists/logout passed using the existing Podman workaround |

Portable results are in `../verification/evidence/phase-1-verification-results.json`. Raw evidence remains in ignored `.phase0/results/` and `.phase1/publish/`. Both packaged verification stacks were stopped with their volumes and bind state retained. The reference tree and `REARCH_PLAN.MD` were not modified.
