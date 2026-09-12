# Phase 7 durable workflows and local executor interfaces

Status: verified, awaiting acceptance.

Verified: 2026-09-11 on `feature/lite-implementation`. Phase 6 is accepted by the request to proceed
with Phase 7. Verification used synthetic IDs and URLs; no credential, cookie, recovery material, or
downloaded media is retained in source control.

## Result

Long-running work now has transport-independent contracts in `Shared.Application`:

- `IDurableWorkflowIngress<TRequest>` accepts work only after its authoritative intent is durable.
- `ILocalWorkExecutor<TWork>` returns an explicit completed/already-completed/retry/cancelled/failed
  result without deciding how a transport message is acknowledged.
- Named download, creator scan, playlist, import, media, search/chat, schedule, and backup contracts
  make every Phase 1 durable path discoverable from a composition root.
- `IArtifactReconciler<TWork>` is the boundary for comparing durable facts with partially written
  external artifacts after cancellation or process loss.
- `ILocalProgressHub<TEvent, TSnapshot>` carries bounded live hints. A subscription is seeded from
  `IPersistedProgressSnapshotStore<TSnapshot>`, so a dropped event or reconnect cannot lose the
  authoritative state.

Download submission is the reference complete extraction:

```text
Full JetStream subscriber -> JetStreamWorkAdapter -> IDownloadWorkflowIngress
future Lite direct call ---------------------------> IDownloadWorkflowIngress
                                                       |
                                                       +-> PostgreSQL job/group/run
                                                       +-> Cleipnir checkpoint
                                                       +-> processed-message marker

JetStream Ack <-------------------------------- durable acceptance receipt
```

Both Full download and group subscribers are transport-only adapters. `DownloadWorkflowIngress`
contains the persistence, duplicate, stale-generation, and Cleipnir-start rules. The adapter acknowledges
only after that operation returns, negatively acknowledges persistence/business failures, and deliberately
leaves a delivery unacknowledged during graceful host cancellation so JetStream can redeliver it.
This preserves Full's broker topology while making acceptance callable without NATS.

The other named executor contracts describe the existing Full handler boundaries without enabling a
second execution path. Their concrete local implementations, claims, concurrency limits, and dispatch
loops are intentionally connected in Phases 10–15. Until then, Full continues to use its existing NATS
handlers and no Lite composition reports those executors as available.

## Recovery matrix

| Work category | Durable intent and checkpoints | Deduplication and retry | Cancellation and artifact reconciliation | Restart behavior |
| --- | --- | --- | --- | --- |
| Downloads | `jobs.download_jobs`, groups/runs/stages/history/progress, processed messages, and Cleipnir flow state | Message ID plus operation key; stage/run attempt state prevents a second logical completion; retry resumes from the last persisted stage | Stop state is persisted before executor cancellation; reconcile temporary download, metadata/sidecar, final storage object, and catalog commit | Reconstruct from PostgreSQL/Cleipnir. A delivery from an older host generation remains visible but paused until explicitly started |
| Creator scans/assets | Creator source, discovered-media records, `jobs.creator_scan_state`, and background-run status | Source plus scan kind/scheduled occurrence; retry the incomplete scan/page or asset independently | Persist cancellation before stopping acquisition; reconcile discovered rows and account/channel assets against source artifacts | Hydrate due/unfinished source work from persisted scan state; never infer acceptance from a wake-up |
| Playlist expansion | Playlist/group records, staged members, child jobs, and playlist Cleipnir state | Playlist/group operation key and provider item identity; retry incomplete pages and child creation idempotently | Cancel group/expansion and children according to persisted policy; reconcile staged items with committed child jobs | Resume the last durable page/checkpoint or remain stopped when explicitly cancelled |
| Imports | Import session/items/options/mappings and import Cleipnir state | Session/item ID and stage attempt; completed item stages are not repeated | Persist session/item cancellation; reconcile scanned/probed metadata, copied temporary files, final object, catalog commit, and optional source deletion | Reconstruct incomplete sessions/items and continue only from their persisted stage |
| Media processing | Audio/stream rendition and thumbnail queue/status rows | Rendition or media/version/format key; claim/attempt state makes completion idempotent | Persist cancelled/failed status; remove or validate temporary ffmpeg output, then reconcile final HLS/audio/thumbnail artifacts before ready | Requeue persisted pending work and repair processing rows whose owning process disappeared |
| Search and chat | PostgreSQL catalog is authoritative for search; Typesense is derived. Chat source sidecars plus ClickHouse ingestion position identify durable input | Media/index revision and source/checkpoint identity; retry upsert/batch without duplicating logical documents or chat rows | Cancellation leaves last committed checkpoint; discard incomplete index batches and reconcile ClickHouse against archived sidecars | Rebuild/replay from authoritative catalog and archived chat sources from the last persisted checkpoint |
| Schedules | `scheduling.scheduled_tasks` plus persisted background-run/attempt state | Schedule key plus due occurrence/idempotency key; failed occurrence uses the persisted retry policy | Disable/cancel is durable before stopping dispatched work; downstream category owns its artifacts | Hydrate active/overdue tasks from PostgreSQL and dispatch missed work once; change signals are wake-ups only |
| Backups | Scheduled occurrence/background status plus backup repository metadata and manifests (the current Full coordinator's in-memory view is not authoritative) | Scheduled idempotency key and repository backup identity; retry only after repository inspection | Persist cancellation request, stop the tool safely, and reconcile pgBackRest/OpenBao/media/derived-data manifest state | Inspect repository and persisted run state, mark interrupted work, then explicitly retry or verify; Phase 15 supplies the local coordinator |

The shared `WorkExecutionResult` makes these terminal boundaries explicit. `Completed` and
`AlreadyCompleted` are successful logical outcomes, `Retry` may include a delay, `Cancelled` records an
accepted cancellation, and `Failed` is terminal until an explicit retry policy creates another attempt.

## Persistence and schema decision

Phase 7 adds no migration. Existing explicit initialization already creates the required PostgreSQL
records: download/group/run/stage/history/progress and processed-message tables, creator scan state,
import sessions/items, rendition queues/status, scheduled tasks/background runs, and Cleipnir's schema.
Typesense remains derived, ClickHouse remains the chat query store, and pgBackRest remains the backup
repository. Those records are sufficient for the interfaces and reference acceptance path.

A generic local-dispatch queue table was deliberately not added. Phase 10 will claim the category's
existing authoritative pending rows while holding the required exclusive PostgreSQL execution lock.
If a concrete executor later proves that an existing category cannot express its claim/checkpoint, that
phase must add the smallest explicit migration rather than using an in-memory queue as durable state.

## Bounded local progress

`BoundedLocalProgressHub` maintains one bounded, drop-oldest channel per subscriber, filters by stream
key, caps requested capacity at 4096, and completes a subscriber when disposed. It registers the live
subscriber before loading the persisted snapshot so events arriving during snapshot retrieval remain
queued. Events are advisory: reconnect always reloads the database-backed snapshot. Phase 10 connects
this interface to the local SSE endpoints and category-specific snapshot stores.

## Verification

Build:

```bash
dotnet build src/App/FrostStream.slnx --no-restore -v:minimal --disable-build-servers
```

Result: succeeded with zero warnings and errors.

Focused unit tests:

```bash
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/*/DurableWorkflowTests/*' --progress off --no-ansi
```

Result: 5/5 passed. The tests cover intent/flow/deduplication ordering, duplicate delivery, no early
acknowledgment, negative acknowledgment on failure, graceful cancellation/redelivery, bounded slow-client
behavior, and persisted snapshot loading.

Complete unit run: 487 tests executed; 483 passed. The same four unrelated baseline assertions failed:
`AccessControlControllerTests`, `EndpointMetadataTests`, `YtDlpFailureDetailsTests`, and
`YtDlpMetadataMapperTests`.

Integration:

```bash
dotnet test --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj \
  --no-build --no-restore
```

Result: 85/85 passed in 43.5 seconds. The added reconstruction test accepts a deliberately stale request,
disposes the service and EF context, reconstructs both, then proves the job/source and processed-message
marker survive and a duplicate does not start another flow.

Full regression used the preserved isolated `froststream-phase5-full` installation. The DataBridge image
was rebuilt and its application containers recreated without deleting volumes. `/health` returned 200.
A temporary authenticated Playwright check submitted a synthetic video URL through WebAPI and observed:

```text
POST /api/downloads/video       202
GET  /api/downloads/queue/{id}  200, persisted status Running
GET  /api/downloads/history     200, DownloadRequested present
POST /api/downloads/{id}/stop   202, subsequently persisted as stopped
```

The exact synthetic job, group, processed-message marker, and two Cleipnir flow instances were then
removed transactionally. Exact-row verification returned `jobs=0`, `groups=0`, `messages=0`, and
`flows=0`. The initialized Full stack and volumes remain running.

## Manual reproduction

1. Start an initialized Full runtime and sign in through Authentik.
2. Obtain `/api/auth/csrf` and send its token in `X-CSRF-TOKEN` for mutations.
3. POST `{"url":"https://example.com/phase7-manual"}` to `/api/downloads/video`; expect 202 with a
   job and correlation ID.
4. GET `/api/downloads/queue/{jobId}` and `/api/downloads/history`; expect the durable row and requested
   event before treating the request as accepted.
5. POST `/api/downloads/{jobId}/stop`; expect 202 and a later persisted stopped status. Clean up only the
   synthetic rows after identifying their exact job/group/flow IDs.

For direct acceptance, resolve `IDownloadWorkflowIngress` from a scope created after
`AddDataBridgeApplicationOperations()` and call `AcceptAsync`. No NATS client or executable
`Program.Main` call is required.

## Known limits and later ownership

This phase extracts contracts and the representative durable ingress; it does not create the merged Lite
host, exclusive executor ownership, local download/worker implementations, media processing, embedded
scheduler, or backup coordinator. Those are Phases 8 and 10–15. The progress hub is not yet connected to
SSE, and concrete category snapshot stores arrive with their endpoints. Full NATS is intentionally retained.

## Rollback

Restore the two Full download ingress services' inline persistence/flow logic, remove the shared durable
contracts and progress registration, and remove the focused tests and inventory matrix. No migration or
data rollback is required. Preserve all Full volumes; do not use `down -v`.
