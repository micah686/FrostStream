# FrostStream Scaling Guide

This guide reflects the current message topology and queue-group layout.

## Current Pipeline

```text
WebAPI
  -> POST /api/videos/download
  -> publish froststream.job.download.file

Worker (1..N)
  -> durable consumer: file-processors
  -> request/reply with DataBridge for job state, storage config, commit, fail, status

DataBridge (1..N)
  -> queue groups for request/reply handlers
  -> PostgreSQL for durable saga state
```

## Queue Groups in Use

| Service | Subject / Consumer | Queue Group / Durable | Purpose |
|---|---|---|---|
| Worker | `froststream.job.download.file` | durable `file-processors` | distribute queued download jobs across workers |
| DataBridge | `froststream.config.storage` | `databridge-config` | load-balance storage config lookups |
| DataBridge | `databridge.job.start` | `databridge-jobs` | reserve / deduplicate job work |
| DataBridge | `databridge.job.progress` | `databridge-jobs` | track saga progress |
| DataBridge | `databridge.video.commit` | `databridge-jobs` | idempotent metadata + version commit |
| DataBridge | `databridge.job.fail` | `databridge-jobs` | mark terminal failures |
| DataBridge | `databridge.job.status` | `databridge-jobs` | serve status queries |
| DataBridge | `databridge.job.link_complete` | `databridge-jobs` | resolve duplicate pending-link jobs |

## Scaling Workers

Workers are stateless apart from the temp download directory and can be replicated horizontally.

Local example:

```bash
cd src/Services/Worker
dotnet run
```

Run that in multiple terminals. Each instance joins the same durable consumer and receives different jobs.

## Scaling DataBridge

DataBridge handlers use queue groups, so multiple instances can safely share NATS request/reply traffic while all of them point at the same PostgreSQL database.

Local example:

```bash
cd src/Services/DataBridge
dotnet run
```

Run additional copies in separate terminals after the first instance is healthy.

## Operational Notes

- `AckWait` for the worker consumer is `5 minutes`
- the worker sends JetStream `InProgress` heartbeats during download, upload, and commit phases
- `VideoCommit` is idempotent by `idempotency_key`
- duplicate requests can resolve through `pending_job_links`
- commit-uncertain retries keep uploaded blobs intact and reissue commit instead of compensating immediately

## Recommended Dev Topology

- `WebAPI`: 1 instance
- `Worker`: 1-3 instances depending on test load
- `DataBridge`: 1-2 instances
- `PostgreSQL`: 1 shared instance
- `NATS`: 1 shared JetStream-enabled instance

## Recommended Production Direction

- scale `Worker` based on download / upload throughput
- scale `DataBridge` based on request/reply load and DB capacity
- keep PostgreSQL highly available before scaling the stateless services aggressively
- add dashboards around `UploadedPendingCommit`, `PendingLink`, retry count, and compensation metrics
