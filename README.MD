# FrostStream

### Main Projects/Services:
- AppHost/ServiceDefaults
- DataBridge
- MediaProcessor
- Scheduler
- Shared
- WebAPI
- Worker


### AppHost:
Does the App Orchestration



## DataBridge:
Manages any storage,DB, and I/O access.
Caching and so forth happens here

## MediaProcessor:
Generate thumbnails
Extract audio streams separately
Transcode formats (FFmpeg operations)
Create video sprites for previews

### Scheduler:
Periodically checks channel RSS/APIs for new videos
Publishes DownloadVideoCommand to NATS
Handles retry logic for "premieres" or delayed uploads
Cleaning failed partial downloads from temp storage
Verifying file integrity against database records
Reconciling "orphaned" Object Store entries (if DB transaction failed but file uploaded)
Archiving old metadata to cold storage


### Shared:
Contains shared logic/types/etc.
DTOs, POCOs,...

### WebAPI:
The main API for the app. Lightweight and simple.

### Worker
Runs the yt-dlp tool, ffmpeg, extracts emotes, metadata.  
May be multiple Agents, are managed by a scheduler/poller

TODO:
does auth/routing/CORS/rate limiting/health checks,...  
Does checks for profiles/account management
does optional emails/notifications




### Deployment topology:

    Orchestrator: 1 replica (singleton), lightweight container (0.5 CPU, 512MB RAM)
    MediaProcessor: 1-10 replicas, heavy containers (4 CPU, 8GB RAM, optional GPU)
    DownloadWorker: Scales with subscription count
    DataBridge: Scales with database connection pool limits
