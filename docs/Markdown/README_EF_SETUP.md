# FrostStream Database and Migration Notes

This document reflects the current `DataBridge` schema and migration flow.

## Overview

`DataBridge` owns the persistent saga state for FrostStream. It runs FluentMigrator on startup and stores:

- storage configuration lookup data
- submitted jobs and retry counters
- per-job tracking state such as uploaded storage path and file hash
- deduplicated video metadata and committed versions
- pending-link records for duplicate requests that should complete when another source job commits

## Current Migrations

- `001_CreateStorageConfigsTable`
- `002_CreateInitialVersionedSchema`
- `003_AddPendingJobLinksTable`

Migrations run automatically every time `DataBridge` starts.

## Main Tables

### `storage_configs`

Storage targets keyed by `storageKey`. The migrations seed a default local target:

```json
{
  "key": "default",
  "method": "PosixLocal",
  "parameters": { "path": "${FROSTSTREAM_STORAGE_ROOT}" }
}
```

### `jobs`

One row per API request / worker job.

Important columns:

- `job_id`
- `url`
- `status`
- `error_msg`
- `retry_count`
- `storage_key`

### `state_tracking`

One row per job for operational saga state.

Important columns:

- `job_id`
- `idempotency_key` (unique)
- `video_id`
- `storage_path`
- `file_hash`
- `updated_at`
- `completed_at`
- `error_details`

### `video_info`

Deduplicated source metadata keyed by the same idempotency domain used by the worker.

### `video_versions`

Committed storage artifacts. `idempotency_key` is unique here as well, which is what enables idempotent commit reconciliation.

### `pending_job_links`

Tracks duplicate requests that should complete by linking to another job's committed version.

Important columns:

- `pending_job_id`
- `source_job_id`
- `existing_version_id`
- `video_id`
- `created_at`
- `completed_at`

## Relationship Summary

- `jobs` 1:1 `state_tracking`
- `video_info` 1:N `video_versions`
- `state_tracking.video_id` -> `video_info.id`
- `pending_job_links.pending_job_id` -> `jobs.job_id`
- `pending_job_links.source_job_id` -> `jobs.job_id`
- `pending_job_links.existing_version_id` -> `video_versions.id`

## Running with AppHost

Recommended for development:

```bash
cd /home/micah/RiderProjects/FrostStream
dotnet run --project src/AppHost/AppHost.csproj
```

This starts PostgreSQL, NATS, DataBridge, Worker, and WebAPI together.

## Running DataBridge Only

If PostgreSQL and NATS are already available:

```bash
cd src/Services/DataBridge
dotnet run
```

Connection-string precedence:

1. Aspire-injected connection string when launched from `AppHost`
2. `appsettings.json` / `appsettings.Development.json`
3. Built-in localhost fallback in `Program.cs`

## Quick Inspection Queries

```sql
SELECT job_id, status, retry_count, storage_key
FROM jobs
ORDER BY job_id DESC
LIMIT 20;
```

```sql
SELECT job_id, idempotency_key, storage_path, file_hash, updated_at, completed_at
FROM state_tracking
ORDER BY updated_at DESC
LIMIT 20;
```

```sql
SELECT pending_job_id, source_job_id, existing_version_id, completed_at
FROM pending_job_links
ORDER BY created_at DESC
LIMIT 20;
```

## Migration Commands

Most development work should use automatic startup migrations. If you need manual migration commands, use `src/Markdown/MIGRATIONS.md`.
