# Phase 0 gate verification — 2026-10-11

These checks exercise the current implementation after Phases 1–7, not the historical Phase 0 binaries. Phase 0 acceptance remains open.

## Lite local storage path

Lite's AppHost development default is `.phase0/data` (a hidden directory). Packaged Lite previously declared `/data` as a project-scoped named volume even though the generator created `<installation>/data`, leaving that visible folder unused. The AppHost now bind-mounts the configured `FROSTSTREAM_STORAGE_ROOT` at `/data`; the generated package resolves it to `<installation>/data`. A fresh profile rendered `LITE_BINDMOUNT_0` to that exact absolute path, passed the package checker, and a container write/remove probe confirmed host-visible write access. `data/archives/` contains downloaded originals; `data/incoming/` is the import staging folder. SQLite and backups also live under `data/`; Lucene stays separately mounted at `/search`.

This change applies to newly generated Compose profiles. Existing Compose files still mount their old named volume. Existing installation data must be copied from that volume into its matching installation `data/` folder before switching its Compose file, so SQLite, keys, backups and archived media stay together. Retained Phase 0/6/7 Lite volumes inspected during this check contained SQLite/application files and no `.mp4`, `.mkv` or `.webm` videos. No retained volume was modified or removed.

Standard Full startup **passes with Docker Compose 5.6.0 against the rootless Podman API**. The normal `up -d --build` command started a new project with new named volumes and separate backup/bootstrap bind directories. All 15 long-lived services ran, the four initializers exited zero, and all five explicit health checks were healthy. Frontend/API capabilities, search readiness, real two-user Authentik OIDC sessions, private-playlist isolation and logout/revocation passed. No dependency removal, `--in-pod=false`, or ordered single-service startup was used. Docker Engine itself is unavailable and was not tested.

The installed `docker compose` command still invokes podman-compose 1.6.0. Its standard startup reproduced the completed-initializer/native dependency failure and stalled. That owned startup process was terminated after the failure; its runner removed the disposable containers and retained volumes. This provider remains incompatible with the standard package.

Distributed media **fails the complete gate**. A real local HTTP source was acquired by production yt-dlp through Worker, NATS and DataBridge. A different video exercised incoming scan, user metadata, inclusion, FFprobe and commit/finalize. Both original files matched SHA-256 and returned correct 16-byte HTTP 206 ranges. Download completion took 5.03 seconds; fresh import took 6.12 seconds. Original-plus-range request pairs took 32.53 and 38.11 ms for the 2,257,003-byte and 2,716,046-byte fixtures. These are single fixture observations, not latency distributions or browser decoding measurements.

HLS generation failed for both videos. MediaProcessor's `MediaProcessorStorageClient` sends no service credentials to `/api/internal/media-storage/...`; `MediaStorageTransferController` declares no authorization policy and falls under `AuthPolicies`' deny-all fallback. Actual MediaProcessor source fetches returned HTTP 401. No authentication bypass was introduced. Successful sustained transcoding and combined download/transcode/index acceptance cannot be measured through this broken path; the required 60-minute combined workload was not run.

The media checker now polls for asynchronous durable job persistence, records original/range evidence, independently checks import after an HLS failure, and checks rendition state before repeating a manifest request. Repeated manifest requests can reset a failed rendition to pending, concealing the failure. Earlier fixture attempts used identical download/import bytes and correctly exercised content deduplication; the final fresh import uses a distinct file and source identity.

Large-corpus measurement uses the deterministic Phase 5 synthetic Lucene fixture: 10,000 media, 100,000 captions and 250,000 comments. It does not seed Full's PostgreSQL/Typesense pipeline or the hash-pinned Phase 0 JSONL fixture. The expanded test measures 20 searches after advisory index-data eviction and can hold the initialized index idle for 900 seconds. Advisory `POSIX_FADV_DONTNEED` is not proof of fully cold filesystem/metadata caches; service initialization occurs before query timing. Full corpus ingestion, verified cold-cache latency and successful combined workloads remain open. See the machine-readable verification evidence for measured values.

The expanded benchmark passed (one opt-in test, 16 minutes 23 seconds). [Machine-readable measurements](../../tooling/performance/current-corpus-measurement.json):

| Measurement | Observed |
| --- | --- |
| Full synthetic indexing | 18.59 seconds |
| Single incremental committed upsert | 3.43 ms |
| Warm search P50 / P95 / P99 (100 requests) | 2.56 / 2.80 / 3.69 ms |
| Advisory-eviction search P50 / P95 / P99 (20 reopens) | 1.32 / 3.02 / 4.38 ms |
| Initialized-index idle (900 seconds) | 0.118 process CPU seconds; 0.0131% of one core |
| Peak test-process RSS | 264.53 MiB |
| Index size | 40.04 MiB |

Cold-labelled queries exclude reopening/initialization; the 3.07-second reopen observation is separate. The lower query times after reopening reinforce why these samples must not be presented as proven cold end-to-end latency. The idle sample includes test-runner background activity, not Full containers.

The development host has 16 logical CPUs and approximately 30.46 GiB RAM. Container accounting includes child processes; host cache/available memory is sampled separately and is not attributed exclusively to FrostStream. These results do not certify the physical 8 GB gate. The container interval includes media checks followed by inactivity, so it is an activity sample rather than a clean 15-minute full-system idle acceptance run. Optional ClickHouse is disabled.

The 900.04-second container interval collected 180 samples: peak aggregate container memory was 2.63 GiB, minimum host available memory was 12.24 GiB, and host `Cached` ranged from 8.97 to 11.61 GiB. Cumulative container CPU increased by 58.92 seconds (6.55% of one core averaged across this mixed interval). Lifetime-average CPU percentages from Podman were not used for this calculation. No successful transcode ran, so this does not characterize FFmpeg load. [Resource summary](../../tooling/performance/current-resource-measurement.json), [startup evidence](evidence/standard-compose-verification.json), and [media evidence](evidence/full-media-verification.json) are committed without credentials; raw samples remain ignored.

Reproduction from `new_refactor_src` (private package files are not committed):

```sh
python3 tooling/installation/package.py Full .phase0/froststream-phase0-standard-v5 --frontend-port 25000
DOCKER_HOST=unix:///run/user/1000/podman/podman.sock /tmp/frost-docker-compose \
  -p froststream-phase0-standard-v5 -f .phase0/froststream-phase0-standard-v5/docker-compose.yaml up -d --build
python3 tooling/installation/check-package.py Full .phase0/froststream-phase0-standard-v5 --base-url http://127.0.0.1:25000
# Generate two distinct FFmpeg videos, serve fixture.mp4 on port 25333,
# and copy import.mp4 to the owned worker's /app/incoming/phase0-import.mp4.
node tooling/installation/check-authentik.mjs --playwright-module /tmp/frost-phase0-browser/node_modules/playwright/index.mjs \
  --env-file .phase0/froststream-phase0-standard-v5/.env \
  --media-url http://host.containers.internal:25333/fixture.mp4 \
  --media-file /tmp/frost-phase0-media/fixture.mp4 --import-media-file /tmp/frost-phase0-media/import.mp4 \
  --output .phase0/results/standard-v5-media-distinct.json
FROSTSTREAM_LUCENE_BENCHMARK_REPORT=/tmp/frost-phase0-corpus-expanded.json \
FROSTSTREAM_BENCHMARK_IDLE_SECONDS=900 dotnet run --no-build --project Tests/UnitTests/UnitTests.csproj \
  -- --treenode-filter '/*/*/LuceneBenchmarkTests/*'
python3 tooling/performance/measure-containers.py --project froststream-phase0-standard-v5 --seconds 900 \
  --output .phase0/results/standard-v5-resources.json
```

The media checker exits nonzero when either HLS check fails, while retaining the successful independent checks and cleaning up browser identities/playlists. Fixture media/storage records remain in the disposable installation for inspection. Raw private build/start logs and resource samples remain under ignored `.phase0/` or `/tmp/`.

After verification, the owned Full stack and fixture HTTP server were stopped. Compose containers were removed without deleting named volumes or bind data. Source architecture verification, the benchmark build/test, JavaScript/Python syntax checks and scoped whitespace checks passed. The Full media checkpoint deliberately remains nonzero because of the observed HLS failures.
