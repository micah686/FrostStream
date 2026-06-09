# Running FrostStream Services

This guide matches the current video-archive pipeline:

1. `POST /api/videos/download` queues a `FileDownloadRequest`
2. `Worker` fetches metadata with `yt-dlp`, downloads the media, uploads it to storage, and asks DataBridge to commit metadata
3. `DataBridge` stores job state, video metadata, versions, duplicate-link relationships, and storage configuration
4. `GET /api/videos/{jobId}` returns the current saga phase and any pending-link details

## Prerequisites

- .NET 10 SDK
- `yt-dlp` available in `PATH` on the machine running `Worker`
- Docker Desktop or another local container runtime if you want to use Aspire AppHost
- For manual startup: reachable NATS and PostgreSQL instances

## Preferred: Run Everything Through AppHost

`AppHost` now orchestrates `DataBridge`, `Worker`, and `WebAPI` together with NATS and PostgreSQL.

```bash
cd /home/micah/RiderProjects/FrostStream
DOTNET_ENVIRONMENT=Development dotnet run --project src/AppHost/AppHost.csproj
```

What AppHost starts:

- `nats` with JetStream enabled
- `postgres` and the `froststreamdb` database
- `databridge`
- `worker`
- `webapi`

Open the Aspire dashboard from the URL shown in the console to find the `webapi` endpoint for requests.

## Manual Startup

Use this path if you already have NATS and PostgreSQL running outside Aspire.

### Terminal 1: DataBridge

```bash
cd src/Services/DataBridge
dotnet run
```

### Terminal 2: Worker

```bash
cd src/Services/Worker
dotnet run
```

### Terminal 3: WebAPI

```bash
cd src/Services/WebAPI
dotnet run
```

Default development URL from `launchSettings.json`:

- `http://localhost:5041`

## Storage Configuration

The storage migrations seed one working storage target:

- `storageKey: default`
- method: `PosixLocal`
- path: `${FROSTSTREAM_STORAGE_ROOT}`

That means the simplest valid request body uses `"storageKey": "default"`.

When run through AppHost, `FROSTSTREAM_STORAGE_ROOT` defaults to the absolute
`data` directory at the repository root. Set the variable before starting
AppHost to override it with another absolute path.

For manual startup, set the same absolute path for every service that accesses
local storage:

```bash
export FROSTSTREAM_STORAGE_ROOT=/absolute/path/to/froststream/data
```

For containers, mount the same persistent volume into every Worker and WebAPI
container at the same path and set:

```text
FROSTSTREAM_STORAGE_ROOT=/var/lib/froststream/data
```

A local filesystem path cannot be shared between different hosts. Use NFS or
object storage when FrostStream services run on multiple machines.

## Queue a Download

```bash
curl -X POST http://localhost:5041/api/videos/download \
  -H "Content-Type: application/json" \
  -d '{
    "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
    "storageKey": "default"
  }'
```

Expected response:

```json
{
  "message": "Download video request queued",
  "jobId": "<guid>",
  "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
  "storageKey": "default"
}
```

## Check Job Status

```bash
curl http://localhost:5041/api/videos/<jobId>
```

The status response now includes:

- `status`: stored job status enum value
- `phase`: higher-level saga phase such as `Downloading`, `Committing`, `Linking`, `Completed`, or `Failed`
- `subStatus`: human-readable detail for the current state
- `storagePath` and `fileHash` when an artifact has already been uploaded
- `pendingLink` details when the request was deduplicated against an existing source job

Common states:

- `Processing`: worker is fetching metadata or downloading
- `UploadedPendingCommit`: artifact uploaded, DataBridge commit is still in-flight or being reconciled
- `PendingLink`: duplicate request is waiting on another source job to finish
- `Completed`: metadata commit succeeded or duplicate-link resolution completed
- `Failed`: terminal failure recorded by DataBridge

## Where Files Land

With the seeded `default` storage config, uploaded artifacts are written under:

```text
${FROSTSTREAM_STORAGE_ROOT}/
```

A typical object path looks like:

```text
<platform>/<videoId>/v<sha256>.<ext>
```

## Notes on Reliability

The current worker keeps JetStream heartbeats alive during long download, upload, and commit phases.
If commit outcome is uncertain, the worker leaves the uploaded artifact in place and retries reconciliation instead of deleting first.

## Scaling Locally

You can start multiple worker processes to share the durable `file-processors` workload:

```bash
cd src/Services/Worker
dotnet run
```

Run that in multiple terminals, then use the same `POST /api/videos/download` endpoint. For the current queue-group layout and scaling notes, see `src/Markdown/SCALING_GUIDE.md`.
