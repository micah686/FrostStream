# Phase 5: Full initialization and runtime lifecycle

Date implemented: 2026-09-11

Status: Implemented, awaiting the complete Full login/messaging/media smoke.

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

Podman Compose's monolithic successful-completion dependency bug prevented using one `up` command for this evidence; the documented ordered path was used. The complete Authentik login, authorization, NATS worker messaging, and media workflow smoke remains the review gate before marking Phase 5 accepted.

## Rollback

Restore unconditional one-shot resource registration and the old combined OpenBao bootstrap behavior only if reverting the lifecycle split. Preserve all named volumes, the pgBackRest host directory, and `openbao-bootstrap/init.env`. Do not use `down -v`; source rollback does not require credential or data deletion.
