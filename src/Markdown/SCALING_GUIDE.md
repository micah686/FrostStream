# FrostStream Scaling Guide

This guide explains how to horizontally scale the Worker and DataBridge services using NATS queue groups.

## Architecture Overview

```
┌─────────┐      NATS       ┌─────────────────────────┐
│ WebAPI  │ ──────────────► │ Workers (queue: workers)│
│         │  jobs.process   │   Instance 1            │
└─────────┘                 │   Instance 2            │
                            │   Instance N...         │
                            └─────────────────────────┘
                                       │
                                       ▼ (request/reply + events)
                            ┌──────────────────────────────────┐
                            │ DataBridge (queue groups)        │
                            │   Instance 1 (config + files)    │
                            │   Instance 2 (config + files)    │
                            │   Instance N...                  │
                            └──────────────────────────────────┘
```

## Queue Groups Explained

**NATS queue groups** ensure that when a message is published to a subject, **only one subscriber in the queue group receives it**. This enables:

1. **Load balancing** - Work is distributed across available instances
2. **High availability** - If one instance dies, others continue processing
3. **Race condition prevention** - Critical operations (like file moves) happen exactly once

### Current Queue Groups

| Service | Subject | Queue Group | Purpose |
|---------|---------|-------------|---------|
| Worker | `jobs.process` | `workers` | Distribute job processing across worker instances |
| DataBridge | `databridge.storage.config` | `databridge-config` | Load balance storage config requests |
| DataBridge | `databridge.file.staged` | `databridge-processors` | Ensure only ONE instance moves each file |

## Scaling Workers

Workers are stateless and designed for horizontal scaling.

### Running Multiple Workers Locally

```bash
# Terminal 1: Worker instance 1
cd src/Services/Worker
dotnet run

# Terminal 2: Worker instance 2
cd src/Services/Worker
dotnet run

# Terminal 3: Worker instance 3
cd src/Services/Worker
dotnet run
```

Each worker:
- Gets a unique `WorkerId` (GUID)
- Joins the `workers` queue group
- NATS distributes jobs across all available workers

### Kubernetes Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: froststream-worker
spec:
  replicas: 5  # Scale to 5 workers
  selector:
    matchLabels:
      app: worker
  template:
    metadata:
      labels:
        app: worker
    spec:
      containers:
      - name: worker
        image: froststream-worker:latest
        env:
        - name: NATS__Url
          value: "nats://nats-service:4222"
        - name: Worker__SourceVideoPath
          value: "/data/video.mp4"
        volumeMounts:
        - name: shared-staging
          mountPath: /staging
      volumes:
      - name: shared-staging
        persistentVolumeClaim:
          claimName: nfs-staging-pvc
```

Scale up/down dynamically:
```bash
kubectl scale deployment froststream-worker --replicas=10
```

## Scaling DataBridge (NEW!)

**Before:** DataBridge was a singleton - only one instance could safely run.

**After:** With NATS queue groups, multiple DataBridge instances can run concurrently without race conditions!

### Running Multiple DataBridge Instances Locally

```bash
# Terminal 1: DataBridge instance 1
cd src/Services/DataBridge
dotnet run

# Terminal 2: DataBridge instance 2
cd src/Services/DataBridge
dotnet run

# Terminal 3: DataBridge instance 3
cd src/Services/DataBridge
dotnet run
```

**What happens:**
- Storage config requests are load-balanced across all instances
- File staged events are distributed - only ONE instance handles each file move
- No race conditions when moving files from staging to final destination

### Kubernetes Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: froststream-databridge
spec:
  replicas: 3  # Run 3 instances for HA and throughput
  selector:
    matchLabels:
      app: databridge
  template:
    metadata:
      labels:
        app: databridge
    spec:
      containers:
      - name: databridge
        image: froststream-databridge:latest
        env:
        - name: NATS__Url
          value: "nats://nats-service:4222"
        - name: DataBridge__StagingPath
          value: "/staging"
        - name: DataBridge__FinalDestination
          value: "/output"
        volumeMounts:
        - name: shared-staging
          mountPath: /staging
        - name: output-storage
          mountPath: /output
      volumes:
      - name: shared-staging
        persistentVolumeClaim:
          claimName: nfs-staging-pvc
      - name: output-storage
        persistentVolumeClaim:
          claimName: nfs-output-pvc
```

## Testing Horizontal Scaling

### Test 1: Load Balancing Workers

1. Start 3 Worker instances
2. Start 1 DataBridge instance
3. Send 10 jobs rapidly:

```bash
for i in {1..10}; do
  curl -X POST http://localhost:5123/api/jobs \
    -H "Content-Type: application/json" \
    -d "{\"destinationPath\": \"output_$i.mp4\"}"
  sleep 0.5
done
```

**Expected:** Jobs are distributed across the 3 workers (check logs for `Worker <guid> picked up job`)

### Test 2: DataBridge High Availability

1. Start 3 DataBridge instances
2. Start 1 Worker instance
3. Send 5 jobs
4. **While jobs are processing**, kill one DataBridge instance (Ctrl+C)
5. Observe: Remaining DataBridge instances continue processing without interruption

### Test 3: Race Condition Prevention

1. Start 2 DataBridge instances with verbose logging
2. Start 1 Worker instance
3. Send 1 job

**Expected:**
- Both DataBridges receive the storage config request, but only ONE responds
- When the file is staged, only ONE DataBridge moves the file
- Check `testing_data/completed/` - file should appear exactly once

## Performance Recommendations

### Development
```
Workers: 1-2 instances
DataBridge: 1-2 instances
```

### Production (Small)
```
Workers: 5-10 instances
DataBridge: 3 instances (HA + moderate throughput)
```

### Production (Large)
```
Workers: 20-50 instances
DataBridge: 5-10 instances
NATS: 3-node cluster
```

### Production (Cloud-Native with S3)
```
Workers: 100+ instances (or serverless)
DataBridge: 10-20 instances (just DB updates, no file moves)
Storage: ObjectStorage (S3/Azure/GCS)
```

## Monitoring Scaling

### Check Queue Group Distribution

```bash
# Install nats CLI
go install github.com/nats-io/natscli/nats@latest

# Monitor the workers queue group
nats sub "jobs.process" --queue=workers

# Monitor the databridge-processors queue group
nats sub "databridge.file.staged" --queue=databridge-processors
```

### Metrics to Track

1. **Queue depth** - How many jobs are pending?
2. **Processing time** - How long does each job take?
3. **Instance count** - How many workers/databridges are active?
4. **Error rate** - Are jobs failing?

## Troubleshooting

### Problem: Jobs aren't distributed evenly

**Cause:** All workers have the same queue group name (this is correct!)

**Solution:** This is expected behavior. NATS distributes based on availability, not round-robin. If you want to see distribution, send jobs faster than workers can process them.

### Problem: Files are missing or duplicated

**Cause:** DataBridge instances aren't using queue groups

**Solution:** Verify `queueGroup: "databridge-processors"` is set in the FileStagedEvent subscription.

### Problem: Multiple DataBridges try to move the same file

**Cause:** Queue group not configured properly

**Solution:** Check logs for `queue: databridge-processors` in the subscription message. If missing, update to latest code.

## Future Enhancements

1. **Auto-scaling** - Scale workers based on NATS queue depth
2. **Geographic distribution** - Workers in multiple regions
3. **Storage-specific scaling** - Different DataBridge pools for different storage methods
4. **Serverless workers** - AWS Lambda, Cloud Run for burst workloads
