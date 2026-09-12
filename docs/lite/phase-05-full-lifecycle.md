# Phase 5: Full initialization and runtime lifecycle

Date implemented: 2026-09-11

Status: Verified, awaiting acceptance.

The Full profiles now publish distinct bundles under one stable Compose project identity. `frostream-full-init` includes six one-shot resources; `froststream-full` omits them and contains no dependency on them.

## Lifecycle contract

The init-only services are:

- `backup-init`: prepares the host backup directories.
- `postgres-init`: creates the FrostStream, Authentik, and OpenFGA databases.
- `openbao-bootstrap`: performs first initialization, persists recovery material, provisions the application token and KV mount, and migrates legacy recovery material.
- `openfga-migrate`: applies the OpenFGA database migrations.
- `backupservice-initialize`: creates and validates the pgBackRest stanza.
- `databridge-initialize`: applies application/workflow schemas, persistent directories, Typesense collections, and the durable compatibility marker.

Every one-shot has `restart: "no"`. PostgreSQL and OpenBao polling is bounded at 120 seconds; application steps and pgBackRest use their Phase 4 deadlines. A nonzero result prevents its declared dependent from starting through `service_completed_successfully`.

The runtime bundle uses PostgreSQL authentication readiness, OpenBao unsealed status, Typesense HTTP readiness, Authentik readiness, and BackupService health. Runtime DataBridge and BackupService retain read-only compatibility checks. PostgreSQL, OpenBao, Typesense, OpenFGA, BackupService, and NATS use recurring restart policies.

OpenBao's runtime wrapper starts the server, waits for restored Raft state, requires the separately persisted `openbao-bootstrap/init.env`, unseals with its recovery key, and only then becomes healthy. It forwards `TERM` and `INT` to the server. An empty runtime volume or missing recovery file exits nonzero with an instruction to run `frostream-full-init` or restore recovery material. Raft may briefly report uninitialized while opening persisted state, so a runtime with recovery material polls rather than misclassifying that transient state.

## Publish and verify the pair

Use the same private env file for both profiles:

```bash
./eng/publish-production-profile.sh \
  frostream-full-init \
  artifacts/full-init \
  /absolute/path/full.env

./eng/publish-production-profile.sh \
  froststream-full \
  artifacts/full-runtime \
  /absolute/path/full.env

dotnet run \
  --project src/App/ComposePublishingFixture.Tests/ComposePublishingFixture.Tests.csproj \
  -- --production-pair \
  artifacts/full-init \
  artifacts/full-runtime \
  froststream-full \
  src \
  src/App/AppHost/deployment/resolved/froststream-full/resolved.env
```

The verifier checks exact init-only membership, absence of runtime dangling dependencies, successful-completion gates, restart policies, health probes, common service image/port/mount parity, stable volumes/network/env contract, portable build contexts, secret non-disclosure, and absence of developer tooling.

## First start and handoff

With Docker Compose, start the init bundle and require all six one-shots to exit zero:

```bash
docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml up -d --build

docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml ps -a
```

Podman Compose 1.6.0 incorrectly treats a successfully exited dependency as an improper container state. On that provider, use this equivalent ordered path; each `run` must exit zero before continuing:

```bash
docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml run --rm --no-deps backup-init

docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml up -d postgres openbao typesense

docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml build \
  backupservice-initialize databridge-initialize

docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml run --rm --no-deps postgres-init
docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml run --rm --no-deps openbao-bootstrap
docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml run --rm --no-deps openfga-migrate
docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml run --rm --no-deps backupservice-initialize
docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml run --rm --no-deps databridge-initialize
```

Stop and remove init containers and recreate from the runtime bundle while retaining every named volume and host recovery directory:

```bash
docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-init/docker-compose.yaml down

docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-runtime/docker-compose.yaml up -d --build

docker compose --env-file /absolute/path/full.env \
  -f artifacts/full-runtime/docker-compose.yaml ps
```

Never add `-v` to the handoff `down` command. It deletes the database, OpenBao, Typesense, and application volumes instead of handing them to runtime.

## Disposable evidence

An isolated `froststream-phase5-smoke` installation used `/tmp/froststream-phase5/state`, installation-prefixed volumes, and ports 33000–33018. Observed results:

- Both profiles passed native Aspire publication and Compose syntax validation with developer tools disabled.
- The graph verifier passed and found exactly the six init-only resources.
- PostgreSQL, OpenBao, and Typesense became healthy on empty disposable state.
- PostgreSQL created all three databases; OpenBao initialized and wrote separate recovery material.
- OpenFGA migration, pgBackRest stanza creation/check, and DataBridge initialization through migration 98 and compatibility version 1 exited zero.
- After removing init containers without volumes, all three PostgreSQL databases remained, Typesense returned healthy, and OpenBao restored Raft state, logged `unsealed from persisted recovery material`, and became healthy.
- Starting runtime OpenBao against empty state exited nonzero with the init-profile instruction, demonstrating the runtime failure gate.

Podman Compose's monolithic successful-completion dependency bug prevented using one `up` command for this evidence; the documented ordered path was used.

## Complete Full application smoke

On 2026-09-11, commit `22763b0e02b1dce997b8aa604d225c6083e2983b` plus the Phase 5 verification fix was exercised as the isolated `froststream-phase5-full` installation. It used synthetic credentials, installation-prefixed volumes, storage and recovery directories under `/tmp/froststream-phase5-full`, frontend port 25000, Authentik port 34100, and WebAPI port 34200. Secrets and recovery material were not copied into this record.

The controlled source was a generated 30-second MP4 containing test video and audio: 7,374,405 bytes, served from a throttled host-only HTTP fixture. The direct-source path was tested; no YouTube/provider or POT claim is made by this run.

The smoke initially found that MediaProcessor's internal GET/PUT storage requests received 401 in multi-user mode. The storage controller correctly fell under the deny-by-default fallback policy, but MediaProcessor sent no credential. The necessary Phase 5 fix adds a dedicated `MEDIA_PROCESSOR_API_KEY`, preserves it with the installation's other generated secrets, injects it only into WebAPI and MediaProcessor, applies an explicit MediaProcessor authentication scheme only to the internal controller, compares keys in fixed time, and fails closed when the key is absent or incorrect. Production hardening requires at least 32 characters. An unauthenticated request to the internal route now returns 401.

Observed application results after rebuilding those two services:

- Anonymous `/api/auth/me` returned 401. Authentik login returned to FrostStream and `/api/auth/me` returned the synthetic `akadmin` owner in multi-user mode.
- Effective access returned bundle `all` and all 194 registered endpoint IDs. The worker registry contained the live Worker heartbeat.
- Job `c876e1c7-4ea1-4664-b5f2-01360509cc67`, correlation `48ae428b-ef6f-4160-8fb9-57d87af033c1`, was stopped during metadata and reached `Stopped`, then restarted with a new run ID. Worker progress and state events arrived through the queue SSE stream. The restarted run downloaded all 7,374,405 bytes and reached the successful duplicate-safe `AlreadyDownloaded` terminal state because an earlier smoke attempt had committed the same fixture.
- The resolved media ID was `e20aaa52-d443-474d-9349-11f84b6e7b08`. Metadata and unique-term search returned it once. A `bytes=0-1023` playback request returned 206 and exactly 1,024 bytes. Watch position 7.5 seconds round-tripped.
- The Opus rendition reached `Ready` at `archives/e20aaa52d443474d934911f84b6e7b08/v1/stream/audio/media.opus`, with 912,445 bytes and a 30-second duration. A ranged audio request returned 206, proving the authenticated MediaProcessor download/upload path.
- Backup job `3da6e39a-9d84-44ec-82fb-a92af28496aa` completed. Repository label `20260912-032452F` contained a 74,108,712-byte database backup, an 8,362,221-byte repository delta, and the OpenBao export.
- A full `down` without `-v`, followed by runtime `up -d`, recreated all 16 runtime containers in about 54 seconds. PostgreSQL retained `authentikdb`, `froststreamdb`, and `openfgadb`; OpenBao logged `unsealed from persisted recovery material`; and PostgreSQL, OpenBao, Typesense, Authentik, and BackupService returned healthy.
- The post-restart browser check retained the Authentik identity, catalog title, one search result, 7.5-second watch position, ready rendition, backup listing, and 206 range playback.
- Three short idle samples after restart measured approximately 2.89 GB total container memory. The largest consumers were ClickHouse at 943–955 MB, Authentik server at 467 MB, Typesense at 345 MB, and Authentik worker at 273 MB. These Full-only measurements are evidence, not a Lite savings target.

Verification commands included:

```bash
dotnet build src/App/FrostStream.slnx --no-restore --verbosity minimal --disable-build-servers

dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/*/MediaProcessorAuthenticationTests/*' \
  --minimum-expected-tests 4 --no-ansi --disable-logo --output Minimal

./eng/publish-production-profile.sh frostream-full-init /tmp/froststream-phase5-fixed/init /tmp/froststream-phase5-full.env
./eng/publish-production-profile.sh froststream-full /tmp/froststream-phase5-fixed/runtime /tmp/froststream-phase5-full.env

dotnet run --project src/App/ComposePublishingFixture.Tests/ComposePublishingFixture.Tests.csproj -- \
  --production-pair /tmp/froststream-phase5-fixed/init /tmp/froststream-phase5-fixed/runtime \
  froststream-phase5-full src \
  src/App/AppHost/deployment/resolved/froststream-phase5-full/resolved.env

docker compose --env-file /tmp/froststream-phase5-full.env \
  -f /tmp/froststream-phase5-full/runtime/docker-compose.yaml down
docker compose --env-file /tmp/froststream-phase5-full.env \
  -f /tmp/froststream-phase5-full/runtime/docker-compose.yaml up -d
```

The solution build passed with zero warnings and errors. The four focused authentication tests passed. The lifecycle publishing verifier passed. The browser lifecycle smoke and post-restart persistence check each passed. The complete 472-test unit run retained the same five unrelated baseline failures recorded in Phase 1; all four new tests were among its 467 passes.

Archived-live-chat replay was unverified because this controlled fixture had no chat. Physical casting was unverified because no compatible device was available. Credentialed non-local storage was unverified because no provider fixture was supplied. Those conditional checks remain explicitly unverified; the mandatory Phase 5 login, authorization, worker messaging, media, backup, and restart lifecycle gate passed.

## Rollback

Restore unconditional one-shot resource registration and the old combined OpenBao bootstrap behavior only if reverting the lifecycle split. Preserve all named volumes, the pgBackRest host directory, and `openbao-bootstrap/init.env`. Do not use `down -v`; source rollback does not require credential or data deletion.
