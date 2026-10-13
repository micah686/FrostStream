# Phase 3 — Reliable and bounded local runtime

The local durable transport now records each required consumer delivery at publication, selects due work through `messaging_inbox_claim`, and claims one item per available execution slot. Handlers run after the claim transaction commits. Known routes remain SDK-free in `ApplicationJobRoutes`; tagged routes are persisted explicitly. Registering a late consumer backfills message identities in pages of 128 and fences retention until backfill completes. Startup repairs unfinished backfills.

Idle consumers use read-only indexed probes, exponential waits from 25 ms to five seconds, and subject-specific broadcast wake-ups. Publication and transactional reply-outbox completion wake matching consumers. Fallback probes recover delayed retries, expired leases and publications from other processes. Lease tokens fence stale acknowledgments and renewals. Queue concurrency defaults to 16 per consumer and 32 across the process, with the caller's batch and acknowledgment limits applied as additional caps.

## Retention and failure handling

Eight maintenance pages run each minute. Each transaction examines at most 128 message identities and 128 deduplication markers; discovery is bounded even when old active work cannot be removed. Payloads and delivery rows are removed only when every registered required delivery is acknowledged and the completed-retention age has passed. Active, terminal and unrouted messages are retained. User-facing application job/history tables are untouched.

Deduplication markers survive payload pruning. Their retention clock advances on completion and duplicate publication. The default window is 30 days; configuration must cover at least the supported seven-day delayed-retry window and completed-message retention. A new consumer cannot request historical replay of payloads that have already completed retention; register required consumers before publishing their work. Unrouted messages and terminal failures require operator attention and are deliberately not discarded automatically.

Unhandled exceptions, malformed payloads, missing acknowledgments and negative acknowledgments count toward the retry limit (eight attempts by default). Automatic retries back off from one to 60 seconds. Exhausted deliveries retain their payload and a bounded error in `messaging_failures`; explicit retry resets that delivery. Shutdown cancellation releases the lease without exhausting the retry budget. Existing generation fencing and interrupted-download reconciliation still require an explicit new run; transport redelivery does not resume an interrupted download.

Configure limits under `Messaging:Local:Queue`, for example:

```json
{
  "MaxAttempts": 8,
  "MaxConsumerConcurrency": 16,
  "MaxConcurrentHandlers": 32,
  "MaxIdleDelay": "00:00:05",
  "CompletedRetention": "1.00:00:00",
  "DedupRetention": "30.00:00:00"
}
```

Inspect or explicitly retry local failures against the running installation's database:

```bash
python3 tooling/operations/local-runtime.py /path/to/froststream.db
python3 tooling/operations/local-runtime.py /path/to/froststream.db --retry-queue 'STREAM/consumer' 123
python3 tooling/operations/local-runtime.py /path/to/froststream.db --retry-index MEDIA_GUID
```

The tool refuses to create a missing database, limits reported failures to 100, and preserves active leases. An external retry is observed within the fallback polling interval. `GetFailuresAsync`, `RetryAsync` and `PendingIndexStore.GetFailuresAsync` also provide programmatic inspection. Full retains index-failure state in the same `search_pending_index` table in PostgreSQL.

## Transactional index intent

Both persistence adapters install `search_pending_index`, due/failure indexes and source triggers after the application schema. These triggers execute inside the originating EF or raw-SQL transaction, covering inserts, updates, deletes, cascades and projection relationships. They track media existence, metadata, comments, captions, stream details, accounts, tags, categories, genres and artists. Playback progress, job history and rendition status are excluded because they do not change the existing search documents.

A monotonic desired revision coalesces each media entity. Tombstone identity remains after source deletion; applied rows retain the revision fence to prevent revision reuse. In Lite the worker waits for the application handler startup gate before resolving stored content. The Typesense worker claims one entity at a time in a short transaction, renews its lease, loads authoritative content and performs provider calls outside database transactions. Comments use indexed keyset pages of at most 128; caption hydration processes one track at a time with a 16 MiB sidecar limit. Missing or oversized supported sidecars fail durable work rather than acknowledging incomplete indexing.

Completion advances only the claimed revision after every provider call succeeds. A newer mutation stays pending. Missing media removes media, comments and captions. Partial provider failures retain work with persisted errors and exponential backoff capped at 60 seconds; retries repeat idempotent replacement/removal. Shutdown releases index leases; process failure leaves them recoverable after the three-minute lease expires. Lost-lease completion requests a fresh authoritative revision to repair stale provider writes.

The former transient sync consumer now requests durable work instead of indexing directly. Signals may be lost without losing committed mutations. The existing rebuild and revision worker share a writer gate; rebuild invalidates durable revisions before clearing collections, preserving replay and tombstones across failure. Atomic replacement generations and rebuild status remain Phase 4/5 work; this phase preserves the existing rebuild behavior. Retain the planned single DataBridge/control-plane owner: its writer gate coordinates rebuilds within that owner, and cross-host rebuild coordination belongs to generation work.

## Media process limits

FFmpeg and ffprobe share a process semaphore. Lite defaults to one child; Full defaults to two per MediaProcessor host. `MediaProcessor:MaxConcurrentProcesses` accepts 1–16. Canceled waiters launch no child, cancellation kills the running process tree, and diagnostic output retains at most 64 KiB per stream. Rendition services pass host cancellation through file/probe/encode work and retain their existing failure-recording and explicit-retry behavior.

## Verification

```bash
python3 tooling/architecture/verify.py
python3 tooling/architecture/verify-final-boundaries.py --require-restored
FROSTSTREAM_PHASE3_IDLE_REPORT="$PWD/.artifacts/test-results/phase3-idle.json" bash tooling/installation/run-checkpoint.sh unit
bash tooling/installation/run-checkpoint.sh lite
bash tooling/installation/run-checkpoint.sh full
python3 tooling/architecture/check-publish-all.py --output .artifacts/publish/reliable-runtime
```

Setting `FROSTSTREAM_TEST_POSTGRES` to a disposable PostgreSQL admin connection enables the same persistence, trigger, revision and killed-writer tests on both providers. See `../verification/evidence/phase-3-verification-results.json` for completed evidence. The idle probe measurement is a controlled single-consumer check, not a full deployment resource benchmark. Phase 0's standard Full Compose, distributed Full media, large-corpus and specified 8 GB machine gates remain open.
