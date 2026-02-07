# Running FrostStream Services

This guide explains how to run the Worker, DataBridge, and WebAPI services to process video files through NATS messaging.

## Prerequisites

- NATS server running on `localhost:4222`
- .NET 10 SDK installed
- A `video.mp4` file in the Worker directory (or configure `Worker:SourceVideoPath`)

## Architecture Overview

```
┌─────────┐      NATS       ┌─────────┐      NATS       ┌────────────┐
│ WebAPI  │ ──────────────► │ Worker  │ ◄─────────────► │ DataBridge │
│         │  jobs.process   │ (1..N)  │  storage.config │            │
└─────────┘                 └─────────┘  file.staged    └────────────┘
                                  │                            │
                                  ▼                            ▼
                            video.mp4                 testing_data/completed/
```

## Starting the Services

Open three terminal windows and run each service from the `src/Services` directory.

### Terminal 1: Start DataBridge

```bash
cd src/Services/DataBridge
dotnet runCan
```

### Terminal 2: Start Worker

```bash
cd src/Services/Worker
dotnet run
```

### Terminal 3: Start WebAPI

```bash
cd src/Services/WebAPI
dotnet run --urls "http://localhost:5123"
```

## Sending a Job Request

Once all services are running, send a POST request to the WebAPI to trigger video processing.

### Using curl

```bash
# Basic request with custom destination filename
curl -X POST http://localhost:5123/api/jobs \
  -H "Content-Type: application/json" \
  -d '{"destinationPath": "my_output.mp4"}'

# Full request with source and destination
curl -X POST http://localhost:5123/api/jobs \
  -H "Content-Type: application/json" \
  -d '{"sourcePath": "video.mp4", "destinationPath": "processed_video.mp4"}'

# Minimal request (uses defaults)
curl -X POST http://localhost:5123/api/jobs \
  -H "Content-Type: application/json" \
  -d '{}'
```

### Using the .http file (Rider/VS Code)

Open `WebAPI/WebAPI.http` and click "Send Request" on any of the examples.

## What Happens

1. **WebAPI** receives the request and publishes a `ProcessJobRequest` to NATS subject `jobs.process`
2. **Worker** picks up the job (queue group ensures only one worker handles each job)
3. **Worker** simulates 5 seconds of processing work
4. **Worker** requests storage configuration from DataBridge via NATS request/reply
5. **DataBridge** responds with `LocalStaging` method and the staging path
6. **Worker** copies `video.mp4` to the staging folder with `.part` extension
7. **Worker** atomically renames `.part` to `.ready` (signals completion)
8. **Worker** publishes `FileStagedEvent` with file path and SHA256 checksum
9. **DataBridge** receives the event and moves the file to `testing_data/completed/`

## Output Location

Processed files appear in:
```
src/Services/testing_data/completed/
```

## Verifying the Output

```bash
# Check the completed folder
ls -la src/Services/testing_data/completed/

# View Worker logs
tail -f /tmp/worker.log

# View DataBridge logs
tail -f /tmp/databridge.log
```

## Running Multiple Workers

To test load balancing, start additional Worker instances in separate terminals:

```bash
# Terminal 4: Second worker
cd src/Services/Worker
dotnet run

# Terminal 5: Third worker (etc.)
cd src/Services/Worker
dotnet run
```

Each worker joins the `workers` queue group, so jobs are distributed across available workers.

## Running Multiple DataBridge Instances (Horizontal Scaling)

**NEW:** DataBridge now supports horizontal scaling using NATS queue groups!

You can run multiple DataBridge instances for high availability and increased throughput:

```bash
# Terminal 6: Second DataBridge instance
cd src/Services/DataBridge
dotnet run

# Terminal 7: Third DataBridge instance (etc.)
cd src/Services/DataBridge
dotnet run
```

Queue groups ensure:
- Storage config requests are load-balanced across instances
- File staged events are distributed - only ONE instance handles each file
- No race conditions when moving files

See [SCALING_GUIDE.md](SCALING_GUIDE.md) for detailed scaling strategies and Kubernetes deployment examples.

## Configuration

### Worker
- `Worker:SourceVideoPath` - Path to source video (default: `video.mp4`)

### DataBridge
- `DataBridge:StagingPath` - Shared staging directory (default: `../testing_data`)
- `DataBridge:FinalDestination` - Output directory (default: `../testing_data/completed`)

### NATS
- `NATS:Url` - NATS server URL (default: `nats://localhost:4222`)

## Stopping Services

Press `Ctrl+C` in each terminal to gracefully stop the services.
