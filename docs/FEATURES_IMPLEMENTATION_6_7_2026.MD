# FrostStream — Capabilities & Feature Inventory

Generated 2026-06-07. Intended as a self-evaluation document: a complete enumeration of what has been implemented so far.

---

## 1. Architecture Overview

| Component | Description |
|-----------|-------------|
| **AppHost** | .NET 10 Aspire orchestrator. Spins up NATS, PostgreSQL, and all services. |
| **WebAPI** | ASP.NET Core REST API. All user-facing HTTP entry points. |
| **Worker** | Background service. Executes yt-dlp, handles storage I/O, filesystem scans. |
| **DataBridge** | Data layer. Owns the database (EF Core + PostgreSQL), runs Cleipnir sagas, and manages all NATS consumers that touch persistence. |
| **Scheduler** | Quartz.NET host. Fires recurring jobs; delegates work to Worker/DataBridge via NATS. |
| **MediaProcessor** | Stub only — no implementation. |
| **FlySwattr.NATS** | Custom NATS wrapper library (Abstractions, Core, Hosting, Resilience, Caching, DistributedLock, Topology). |
| **YtDlpSharpLib** | Local fork of the yt-dlp .NET wrapper with custom download/progress models. |

**Infrastructure:**
- NATS with JetStream for durable messaging and background job streams
- PostgreSQL for all persistent state
- Typesense for full-text search (3 collections: media, comments, captions)
- OpenBAO (Vault-compatible) for secrets (cookie files, storage credentials)
- FluentStorage abstraction over local disk, FTP/SFTP, S3-compatible, Azure Blob, and GCS

---

## 2. WebAPI — HTTP Endpoints

All controllers are under `api/[controller]` or explicitly routed as shown.

### 2.1 Downloads (`/api/downloads`)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/downloads` | Submit a video download. Fields: `sourceUrl`, `storageKey`, `forceDownload`, `requestedBy`, `tags`, `cookieKey`. Returns `jobId` + `correlationId` (202 Accepted). |
| `POST` | `/api/downloads/audio` | Submit an audio-only download. Same fields as above. Worker forces `--extract-audio --audio-format mp3`. |
| `POST` | `/api/downloads/preset` | Submit a download using a stored yt-dlp option preset. Fields: `sourceUrl`, `storageKey`, `forceDownload`, `requestedBy`, `tags`, `presetKey`, `cookieKey`. |

All three publish a `DownloadRequested` JetStream event and return immediately. Progress is tracked via the download saga (not yet exposed as a polling endpoint).

**Download Job States:** `Queued → MetadataPending → DownloadPending → UploadPending → CommitPending → Completed` (happy path). Failure states: `FailedTransient`, `FailedPermanent`, `DeadLettered`, `AlreadyDownloaded`.

### 2.2 Playlists (`/api/playlists`)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/playlists` | Submit a playlist URL for download. Fields: `sourceUrl`, `requestedBy`, `storageKey`. Returns `playlistId` + `correlationId` (202). |
| `GET` | `/api/playlists` | List all playlists, newest first. Query params: `pageSize` (default 50), `pageOffset`. |
| `GET` | `/api/playlists/{id:guid}` | Get a single playlist by ID, including the full item list. |

### 2.3 Storage (`/api/storage`)

Manages FluentStorage backend configurations. Credentials are stored in OpenBAO; the DB holds non-secret parameters.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/storage/local/create` | Create a local filesystem storage config. Fields: `key`, `description`, `protocol`, `path`. |
| `PUT` | `/api/storage/local/update/{key}` | Update a local storage config. |
| `POST` | `/api/storage/network/create` | Create a network storage config (FTP/FTPS/SFTP). Fields: `key`, `description`, `protocol`, `host`, `port`, `username`, `basePath`, plus credentials. |
| `PUT` | `/api/storage/network/update/{key}` | Update a network storage config. |
| `POST` | `/api/storage/object/s3-compatible/create` | Create S3-compatible object storage (AWS S3, MinIO, R2, etc.). Fields: `key`, `description`, `provider`, `bucketName`, `region`, `endpoint`, `forcePathStyle`, `useSsl`, plus credentials. |
| `PUT` | `/api/storage/object/s3-compatible/update/{key}` | Update S3-compatible object storage. |
| `POST` | `/api/storage/object/azure-blob/create` | Create Azure Blob Storage config. Fields: `key`, `description`, `containerName`, `azureAccountName`, `credentialMode`. |
| `PUT` | `/api/storage/object/azure-blob/update/{key}` | Update Azure Blob Storage. |
| `POST` | `/api/storage/object/google-cloud-storage/create` | Create GCS config. Fields: `key`, `description`, `bucketName`, `credentialMode`, `gcpProjectId`, `gcpCredentialsFilePath`. |
| `PUT` | `/api/storage/object/google-cloud-storage/update/{key}` | Update GCS config. |
| `GET` | `/api/storage/list` | List all storage configurations (non-secret fields only). |
| `GET` | `/api/storage/{key}` | Get a single storage configuration by key. |
| `DELETE` | `/api/storage/delete/{key}` | Delete a storage configuration. |

### 2.4 Schedules (`/api/schedules`)

Manages DB-backed scheduled tasks that the Scheduler service hydrates into Quartz.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/schedules` | Create a scheduled task. Fields: `key`, `taskType`, `cron`, `intervalSeconds`, `timezone`, `enabled`, `catchupPolicy`. |
| `PUT` | `/api/schedules/{key}` | Update a scheduled task. Same fields, minus `key` (in path). |
| `GET` | `/api/schedules/{key}` | Get a scheduled task by key. |
| `GET` | `/api/schedules` | List all scheduled tasks. |
| `DELETE` | `/api/schedules/{key}` | Delete a scheduled task. |

**Registered Task Types:** `channel_update_check`, `channel_media_list`, `channel_asset_refresh`, `database_maintenance`, `filesystem_rescan`, `processed_message_cleanup`, `search_reindex`, `stale_database_cleanup`.

### 2.5 Option Presets (`/api/option-presets`)

Stores named yt-dlp option snapshots for reuse in download requests.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/option-presets` | Create a preset. Fields: `key`, `name`, `description`, `ytDlpOptions` (full `YtDlpOptions` object). |
| `PUT` | `/api/option-presets/{key}` | Update a preset. |
| `GET` | `/api/option-presets/{key}` | Get a preset by key (returns deserialized `YtDlpOptions`). |
| `GET` | `/api/option-presets` | List all presets. |
| `DELETE` | `/api/option-presets/{key}` | Delete a preset. |

### 2.6 Creator Sources (`/api/creator-sources`)

Manages creator channel/account sources that the Scheduler periodically scans for new content.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/creator-sources` | Create a creator source. Fields: `platform`, `sourceType`, `sourceUrl`, `scanEnabled`, `incrementalPageSize`, `consecutiveKnownThreshold`, `fullRescanIntervalDays`, `metadataRefreshWindow`. |
| `PUT` | `/api/creator-sources/{id:long}` | Update a creator source. |
| `GET` | `/api/creator-sources/{id:long}` | Get a creator source by ID. |
| `GET` | `/api/creator-sources` | List all creator sources. |
| `POST` | `/api/creator-sources/{id:long}/refresh-assets` | Manually trigger avatar/banner asset refresh for a source. Query param: `force` (bypass freshness window). Returns 202. |
| `DELETE` | `/api/creator-sources/{id:long}` | Delete a creator source. |

### 2.7 Cookies (`/api/cookies`)

Manages Netscape-format cookie files stored in OpenBAO. Used to authenticate yt-dlp downloads for member-only content.

| Method | Path | Description |
|--------|------|-------------|
| `PUT` | `/api/cookies/{key}` | Upsert a cookie file. Body: `{ "content": "<netscape cookie file text>" }`. Key must match `^[a-z0-9-]{2,100}$`. |
| `GET` | `/api/cookies/{key}` | Confirm a cookie exists (metadata only — body never returned over HTTP). |
| `DELETE` | `/api/cookies/{key}` | Delete a cookie. |

### 2.8 Metadata — Read (`/api/metadata`)

All queries go via NATS request/reply to DataBridge (10-second timeout).

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/metadata` | Paged media listing. Query params: `pageSize` (24), `page` (1), `sortBy` (`release_date`), `sortOrder` (`desc`), `platform`, `accountId`, `tag`, `category`, `genre`, `captionLanguage`. |
| `GET` | `/api/metadata/search?q=` | Full-text search via Typesense. Params: `q` (required), `pageSize`, `page`, `platform`, `tag`, `category`, `genre`, `sortBy`, `sortOrder`. |
| `GET` | `/api/metadata/{guid:guid}` | Get full media detail for a single item. |
| `GET` | `/api/metadata/{guid:guid}/technical` | Get technical metadata (format, streams, chapters). |
| `GET` | `/api/metadata/{guid:guid}/comments` | List/search comments for a media item. Params: `pageSize`, `page`, `q`, `parentId`, `sortBy`, `sortOrder`. |
| `GET` | `/api/metadata/{guid:guid}/captions` | List captions for a media item. Params: `languageCode`, `captionType`. |
| `GET` | `/api/metadata/accounts` | List accounts (cursor-paged). Params: `pageSize`, `after`, `platform`. |
| `GET` | `/api/metadata/accounts/{accountId:long}` | Get a single account. |
| `GET` | `/api/metadata/accounts/{accountId:long}/media` | List media for an account (paged). Params: `pageSize`, `page`, `sortBy`, `sortOrder`. |
| `GET` | `/api/metadata/taxonomy/tags` | List all tags (searchable). Params: `pageSize`, `pageOffset`, `search`. |
| `GET` | `/api/metadata/taxonomy/categories` | List all categories (searchable). |
| `GET` | `/api/metadata/taxonomy/genres` | List all genres (searchable). |

### 2.9 Metadata — Admin (`/api/metadata`)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/metadata/reindex` | Trigger a full Typesense search index rebuild. Publishes a background job to JetStream. Returns 202. |
| `GET` | `/api/metadata/orphans` | List orphaned media items tracked in `orphan_cleanup_items`. Params: `kind`, `state`, `pageSize`, `page`. |
| `POST` | `/api/metadata/orphans/{id:long}/restore-file` | Move an orphaned file from the `orphaned/` prefix back to its original storage path. |
| `POST` | `/api/metadata/orphans/{id:long}/restore-metadata` | Re-link orphan metadata to the media record. |

---

## 3. Worker — Capabilities

The Worker is a JetStream consumer service with no HTTP surface. All communication is via NATS.

### 3.1 Download Flow Commands

Subscribes to 5 durable JetStream consumers on the `FROSTSTREAM_DOWNLOAD` stream:

| Consumer | Command Message | What It Does |
|----------|-----------------|--------------|
| `worker-fetch-metadata` | `FetchMetadataCommand` | Invokes yt-dlp `--dump-json` on the source URL. Materializes cookies from OpenBAO. Maps the `VideoInfo` to `CapturedMediaMetadata`. Publishes `MetadataFetched` or `MetadataFetchFailed`. |
| `worker-download-video` | `DownloadVideoCommand` | Invokes yt-dlp to download to a worker-local temp directory. Computes XxHash128 of the output file. Also picks up any `.info.json` sidecar if caller requested `--write-info-json`. Publishes `DownloadCompleted` or `DownloadFailed`. |
| `worker-upload-object` | `UploadObjectCommand` | Reads the temp file and writes it to the FluentStorage backend at the path DataBridge reserved. Publishes `UploadCompleted` or `UploadFailed`. |
| `worker-delete-temp-file` | `DeleteTempFileCommand` | Deletes the worker-local temp file (and empty parent directories under `/tmp/froststream`). Publishes `TempFileDeleted` or `TempFileDeleteFailed`. Path is validated to stay within the FrostStream temp directory. |
| `worker-delete-uploaded-object` | `DeleteUploadedObjectCommand` | Compensating action: deletes an already-uploaded object from storage. Publishes `UploadedObjectDeleted` or `UploadedObjectDeleteFailed`. |

**yt-dlp operational defaults applied by the Worker regardless of caller options:**
- `NoPlaylist = true` (one file per job)
- `NoPart = true` (atomic writes)
- `Newline = true` (readable progress output)

**Progress events:** The Worker publishes `DownloadProgress` events to `download.evt.download-progress` while yt-dlp is running. These are not currently consumed by DataBridge (see the TODO in `DownloadSubjects.cs`) — intended for live diagnostic subscribers.

### 3.2 Playlist Flow Commands

Subscribes to 1 durable consumer on the `FROSTSTREAM_PLAYLIST` stream:

| Consumer | Command | What It Does |
|----------|---------|--------------|
| `worker-fetch-playlist-metadata` | `FetchPlaylistMetadataCommand` | Runs yt-dlp flat scan on the playlist URL. First tries the top-level metadata dump (entries embedded). Falls back to streaming `GetPlaylistInfoAsync` for extractors that return entries piecemeal. Publishes `PlaylistMetadataFetched` (with index + URL for each entry) or `PlaylistMetadataFetchFailed`. |

Per-entry downloads are dispatched as ordinary `DownloadRequested` messages by DataBridge after `PlaylistMetadataFetched` arrives.

### 3.3 Channel Discovery

Subscribes to 2 durable consumers on the `FROSTSTREAM_BACKGROUND_JOBS` stream:

| Consumer | Trigger | Scan Mode | What It Does |
|----------|---------|-----------|--------------|
| `worker-channel-update-check` | `ChannelUpdateCheckRequested` | Incremental | Fetches the most recent `IncrementalPageSize` entries per source (yt-dlp `--playlist-items 1:N`). Upserts discovered media candidates to DataBridge. |
| `worker-channel-media-list` | `ChannelMediaListRequested` | Full | Full flat scan of every creator source. No item limit. |

Both modes:
1. Mark schedule attempt in DataBridge.
2. Ask DataBridge for all `scan_enabled = true` sources.
3. For each source, run yt-dlp flat scan.
4. Send `UpsertDiscoveredMediaBatch` to DataBridge.
5. Mark schedule success.

**Candidate extraction:** Pulls `externalMediaId`, `canonicalUrl`, `title`, `durationSeconds`, `thumbnailUrl`, `liveStatus`, `availability` from each flat-scan entry. Has YouTube-specific URL fallback (`https://www.youtube.com/watch?v={id}`).

### 3.4 Channel Asset Refresh

Subscribes to 1 durable consumer on the `FROSTSTREAM_BACKGROUND_JOBS` stream:

| Consumer | Trigger | What It Does |
|----------|---------|--------------|
| `worker-channel-asset-refresh` | `ChannelAssetRefreshRequested` | Downloads avatar and banner thumbnails from yt-dlp metadata for each creator source. Skips sources whose assets were refreshed within the configured `FreshnessWindow`. Stores result paths and hashes in DataBridge via `UpdateCreatorSourceAssets`. |

**Thumbnail selection logic:**
- Avatar: prefers thumbnails whose ID starts with `avatar` (or `avatar_uncropped`), then falls back to near-square aspect ratio (0.9–1.1).
- Banner: prefers thumbnails whose ID starts with `banner`, then falls back to wide aspect ratio (≥3.0).

Can target a single source (via `TargetSourceId`) or all enabled sources.

### 3.5 Filesystem Rescan

Subscribes to 1 durable consumer on the `FROSTSTREAM_BACKGROUND_JOBS` stream:

| Consumer | Trigger | What It Does |
|----------|---------|--------------|
| `worker-filesystem-rescan` | `FilesystemRescanRequested` | Enumerates every file in every configured storage backend. Serializes paths as NDJSON and uploads to a NATS object store bucket. Requests DataBridge to reconcile the listing against the database (`FilesystemRescanSubjects.Reconcile`). Reports `MissingCount` (in DB but not on disk) and `UnexpectedCount` (on disk but not in DB). |

Paths are normalized before upload (`StoragePathNormalizer.Normalize`). A per-message NATS heartbeat fires every 10 seconds to keep JetStream from timing out during long-running scans.

### 3.6 Orphan Cleanup (NATS Request/Reply)

Handles 4 request/reply subjects from DataBridge:

| Subject | Request | What It Does |
|---------|---------|--------------|
| `orphan.worker.move-file` | `MoveOrphanedFileRequest` | Moves a file from its original storage path to `orphaned/{...}`. Validates that destination starts with `orphaned/`. |
| `orphan.worker.delete-file` | `DeleteOrphanedFileRequest` | Permanently deletes a file from the `orphaned/` prefix. |
| `orphan.worker.restore-file` | `RestoreOrphanedFileRequest` | Moves a file from `orphaned/` back to its original path. Checks for conflict (destination already exists). |
| `orphan.worker.file-exists` | `OrphanFileExistsRequest` | Checks whether a file exists at a given storage path. |

---

## 4. DataBridge — Capabilities

DataBridge is the persistence hub. It owns PostgreSQL, runs Cleipnir resilient flows, and handles all NATS-facing data operations.

### 4.1 Download Saga (Cleipnir `DownloadArchiveFlow`)

A durable, resumable saga per `jobId`. Steps:

1. **Preset resolution** — if `PresetKey` is set and `YtDlpOptions` is not, look up the preset in `download_option_presets` and hydrate the request.
2. **State: Queued** — job row created by the ingress service before the flow starts.
3. **Metadata step** (up to 3 attempts):
   - Dispatches `FetchMetadataCommand` to Worker.
   - Awaits `MetadataFetched` or `MetadataFetchFailed`.
   - Classifies failures as Permanent/Transient and retries or terminates.
4. **Dedupe by source** — checks `(provider, sourceMediaId, sourceLastModified)` in the DB. Short-circuits to `AlreadyDownloaded` if present (unless `forceDownload = true`).
5. **Download step** (up to 3 attempts):
   - Dispatches `DownloadVideoCommand`.
   - Awaits `DownloadCompleted` or `DownloadFailed`.
   - Dispatches temp file cleanup if download fails with partial bytes.
6. **Version reservation** — content-hash (XxHash128) dedupe. If the exact bytes are already stored, skips upload and reuses the existing `media_guid`.
7. **Upload step** (up to 3 attempts):
   - Dispatches `UploadObjectCommand` with the DataBridge-computed storage path (`archives/{mediaGuid}/v{n}/{filename}`).
   - Awaits `UploadCompleted` or `UploadFailed`.
8. **DB commit** — marks job as `Completed`, links to playlist if applicable.
9. **Sidecar upload** (best-effort) — uploads the `.info.json` next to the video if the Worker produced one.
10. **Rich metadata write** — writes `CapturedMediaMetadata` to all `metadata.*` tables. Publishes `MetadataSyncUpsertMessage` to trigger Typesense sync.
11. **Temp file cleanup** — dispatches `DeleteTempFileCommand`.

**Compensation:** failed uploads trigger deletion of the reserved version row and the uploaded object; failed commits trigger full rollback including DB rows.

### 4.2 NATS Consumers

| Service | Subjects Handled | Role |
|---------|-----------------|------|
| `DownloadRequestedIngressService` | `download.requested` | Dedupes and starts Cleipnir flow per job. |
| `DownloadEventsConsumerService` | 10 subjects (`download.evt.*`) | Applies state changes to `download_jobs`, appends to `download_job_history`, forwards events to Cleipnir. Runs 10 consumers in parallel. |
| `PlaylistRequestedIngressService` | `playlist.requested` | Starts playlist flow. |
| `PlaylistEventsConsumerService` | `playlist.evt.*` | Applies playlist state changes. |
| `PlaylistQueryConsumerService` | `playlist.list`, `playlist.get` | Queries playlists from DB. |
| `StorageCrudConsumerService` | `storage.*` (CRUD + list + get) | Manages storage configs in DB and credentials in OpenBAO. |
| `OptionPresetCrudConsumerService` | `preset.*` (CRUD) | Manages yt-dlp option presets in DB. |
| `ScheduleCrudConsumerService` | `schedule.*` (CRUD + mark-attempt/success/failure) | Manages scheduled task records in DB. Notifies Scheduler of changes. |
| `CreatorDiscoveryConsumerService` | `creator.*` (CRUD + upsert-batch + update-assets) | Manages creator sources and discovered media candidates. |
| `BackgroundJobConsumerService` | Background job subjects | Routes background job requests to internal handlers. |
| `FilesystemRescanConsumerService` | `fs.rescan.*` | Reads the NDJSON listing uploaded by the Worker and reconciles against `download_jobs` / `filesystem_rescan_findings`. |
| `OrphanMetadataCleanupConsumerService` | `orphan.*` (metadata) | Detects and cleans up metadata rows without corresponding storage objects. |
| `OrphanCleanupAdminConsumerService` | `orphan.admin.*` | Handles list/restore-file/restore-metadata admin requests. |
| `MetadataQueryConsumerService` | `metadata.*` (all read subjects) | Queries media metadata, accounts, taxonomy, technical details, comments, captions from DB. |
| `MetadataSearchConsumerService` | `metadata.search` | Routes to Typesense for full-text search. |
| `MetadataCaptionsConsumerService` | Typesense captions sync | Syncs captions to Typesense. |
| `MetadataCommentsConsumerService` | Typesense comments sync | Syncs comments to Typesense. |
| `MetadataListConsumerService` | Typesense media listing | Serves metadata list requests from Typesense. |
| `MetadataRebuildConsumerService` | `fs.index.search.rebuild.*` | Drives full Typesense rebuild from DB. |
| `TypesenseSyncConsumerService` | `metadata.sync.upsert` | Per-document Typesense upsert after a new download completes. |

### 4.3 Database Schema (FluentMigrator migrations 001–020)

| Migration | Tables Created |
|-----------|---------------|
| 001 | `public.storage_keys` |
| 002 | Drop plaintext credentials columns |
| 003 | `public.download_jobs`, `public.download_job_history`, `public.processed_messages` |
| 004 | Cleipnir schema (flow state persistence) |
| 005 | `public.media_content_id_versions`, `public.media_root` (early version — superseded) |
| 006 | `metadata` schema: `accounts`, `media_metadata`, `media_base`, `media_format_details`, `media_streams`, `video_stream_details`, `audio_stream_details`, `chapter_data`, `media_captions`, `media_comments`, `series_metadata`, `music_metadata`, `artists`, `genres`, `tags`, `categories`, `cast_members`, junction tables (`media_artists`, `media_genres`, `media_tags`, `media_categories`, `media_cast`) |
| 007 | `public.media_root` (final version) |
| 008 | `public.playlists`, `public.playlist_items` |
| 009 | `public.playlist_metadata` |
| 010 | `public.download_option_presets` |
| 011 | `public.scheduled_tasks` |
| 012 | Seed: monthly maintenance scheduled tasks (database_maintenance, stale_database_cleanup, filesystem_rescan, search_reindex) |
| 013 | `public.creator_sources`, `public.discovered_media` |
| 014 | Add avatar/banner/asset columns to `creator_sources` |
| 015 | Add `info_json_*` columns to `download_jobs` |
| 016 | Add `run_status` column to `scheduled_tasks` |
| 017 | Seed: `processed_message_cleanup` scheduled task |
| 018 | `public.filesystem_rescan_findings` |
| 019 | Path normalization columns on `filesystem_rescan_findings` |
| 020 | `public.orphan_cleanup_items` |

### 4.4 Rich Metadata Model (`CapturedMediaMetadata`)

Written for each ingested item:

| Section | Fields |
|---------|--------|
| **Account** | `platform`, `accountName`, `accountHandle`, `accountUrl`, `followerCount`, `description` |
| **Media (core)** | `externalMediaId`, `metadataScrapeDate`, `thumbnailStoragePath`, `ageLimit`, `averageRating`, `likeCount`, `dislikeCount`, `durationSeconds`, `description`, `releaseDate`, `title`, `wasLive`, `webpageUrl`, `viewCount`, `commentCount`, `availability`, `location` |
| **Technical** | `durationTicks`, format (`formatLongNames`, `streamCount`, `bitRate`, `startTimeTicks`), streams (type, codec, bitRate, language), video detail (resolution, frameRate, HDR, color space), audio detail (channels, sampleRate), chapters |
| **Captions** | Per language: `storagePath`, `captionType` (`subtitles`/`automatic_captions`), `languageCode`, `name` |
| **Comments** | `commentId`, `parentCommentId`, `text`, commenter account, `commentTimestamp`, `likeCount`, `dislikeCount`, `isFavorited`, `isPinned` |
| **Series** | `seriesName`, `seasonCount`, `seasonNumber`, `seasonName`, `episodeNumber`, `episodeName` |
| **Music** | `albumTitle`, `albumType`, `discNumber`, `releaseYear`, `trackTitle`, `trackNumber`, `composer` |
| **Taxonomy** | `genres[]`, `tags[]`, `categories[]`, `cast[]`, `artists[]`, `albumArtists[]` |

### 4.5 Typesense Collections

| Collection | Document Type | Key Fields |
|------------|--------------|------------|
| `media` | `MediaDocument` | `mediaGuid`, `title`, `description`, `platform`, `accountName`, `releaseDate`, `tags`, `categories`, `genres` |
| `comments` | `CommentDocument` | `mediaGuid`, `commentId`, `text`, `accountName`, `timestamp` |
| `captions` | `CaptionDocument` | `mediaGuid`, `languageCode`, `captionType`, caption text |

Managed by `TypesenseIndexService`. Ensures collections on startup. Supports bulk import (batch size 500) and per-document upsert. Full rebuild recreates all 3 collections from DB.

---

## 5. Scheduler — Capabilities

Uses Quartz.NET with a CrystalQuartz visual dashboard.

### 5.1 Registered Jobs

| Job Class | Task Type Key | What It Does |
|-----------|--------------|--------------|
| `ChannelUpdateCheckJob` | `channel_update_check` | Publishes `ChannelUpdateCheckRequested` to JetStream. Worker performs incremental scan of all enabled creator sources. |
| `ChannelMediaListJob` | `channel_media_list` | Publishes `ChannelMediaListRequested`. Worker performs full scan. |
| `ChannelAssetRefreshJob` | `channel_asset_refresh` | Publishes `ChannelAssetRefreshRequested` (no `TargetSourceId` = all sources). Worker refreshes avatar/banner assets. |
| `DatabaseMaintenanceJob` | `database_maintenance` | Publishes `DatabaseMaintenanceRequested`. DataBridge runs VACUUM/ANALYZE or equivalent maintenance. |
| `FilesystemRescanJob` | `filesystem_rescan` | Publishes `FilesystemRescanRequested`. Worker enumerates storage; DataBridge reconciles. |
| `ProcessedMessageCleanupJob` | `processed_message_cleanup` | Publishes `ProcessedMessageCleanupRequested`. DataBridge purges old rows from `processed_messages`. |
| `SearchReindexJob` | `search_reindex` | Publishes `SearchReindexRequested`. DataBridge rebuilds Typesense from Postgres. |
| `StaleDatabaseCleanupJob` | `stale_database_cleanup` | Publishes `StaleDatabaseCleanupRequested`. DataBridge removes stale/orphaned DB entries. |

All jobs use `[DisallowConcurrentExecution]`.

### 5.2 Schedule Management

- **`ScheduleHydrationService`** — on startup, loads all enabled schedules from DataBridge via NATS and registers them in Quartz.
- **`ScheduleChangeListener`** — subscribes to schedule CRUD events; dynamically adds, updates, or removes Quartz triggers at runtime without restart.
- **`QuartzJobRegistrar`** — translates a `ScheduledTaskDto` (cron string or interval seconds + timezone + catchup policy) into a Quartz `IJobDetail` + `ITrigger`.
- **`DatabridgeClient`** — thin NATS client used by the Scheduler to query DataBridge for schedule data.
- **`OrphanMetadataCleanupTriggerJob`** — special trigger job that DataBridge uses to self-schedule the orphan metadata cleanup without a Scheduler round-trip.
- **CrystalQuartz dashboard** — optional visual UI for Quartz job/trigger inspection.

---

## 6. NATS Messaging — Subject Map

### Download Flow
| Subject | Direction | Meaning |
|---------|-----------|---------|
| `download.requested` | WebAPI → DataBridge | New download request |
| `download.cmd.fetch-metadata` | DataBridge → Worker | Fetch source metadata |
| `download.cmd.download-video` | DataBridge → Worker | Download source to temp |
| `download.cmd.upload-object` | DataBridge → Worker | Upload temp to storage |
| `download.cmd.delete-temp-file` | DataBridge → Worker | Clean up temp file |
| `download.cmd.delete-uploaded-object` | DataBridge → Worker | Compensating storage delete |
| `download.evt.metadata-fetched` | Worker → DataBridge | Metadata success |
| `download.evt.metadata-fetch-failed` | Worker → DataBridge | Metadata failure |
| `download.evt.download-progress` | Worker → (open) | Live progress (not yet consumed by DataBridge) |
| `download.evt.download-completed` | Worker → DataBridge | Download success |
| `download.evt.download-failed` | Worker → DataBridge | Download failure |
| `download.evt.upload-completed` | Worker → DataBridge | Upload success |
| `download.evt.upload-failed` | Worker → DataBridge | Upload failure |
| `download.evt.temp-file-deleted` | Worker → DataBridge | Temp cleanup success |
| `download.evt.temp-file-delete-failed` | Worker → DataBridge | Temp cleanup failure |
| `download.evt.uploaded-object-deleted` | Worker → DataBridge | Compensating delete success |
| `download.evt.uploaded-object-delete-failed` | Worker → DataBridge | Compensating delete failure |

### Playlist Flow
| Subject | Direction | Meaning |
|---------|-----------|---------|
| `playlist.requested` | WebAPI → DataBridge | New playlist request |
| `playlist.cmd.fetch-metadata` | DataBridge → Worker | Fetch flat playlist metadata |
| `playlist.evt.metadata-fetched` | Worker → DataBridge | Playlist entries resolved |
| `playlist.evt.metadata-fetch-failed` | Worker → DataBridge | Playlist fetch failed |

### Background Jobs (Scheduler → Worker/DataBridge)
| Subject | Consumer |
|---------|----------|
| `fs.channel.update-check.request` | Worker (incremental scan) |
| `fs.channel.media-list.request` | Worker (full scan) |
| `fs.channel.asset-refresh.request` | Worker (asset download) |
| `fs.media.filesystem.rescan.request` | Worker (storage enumeration) |
| `fs.cleanup.database.stale.request` | DataBridge |
| `fs.cleanup.database.processed-messages.request` | DataBridge |
| `fs.cleanup.database.maintenance.request` | DataBridge |
| `fs.index.search.rebuild.request` | DataBridge (Typesense rebuild) |

### Metadata (WebAPI → DataBridge, request/reply)
`metadata.list`, `metadata.get`, `metadata.get-technical`, `metadata.comments.list`, `metadata.captions.list`, `metadata.search`, `metadata.accounts.list`, `metadata.accounts.get`, `metadata.accounts.media.list`, `metadata.taxonomy.tags.list`, `metadata.taxonomy.categories.list`, `metadata.taxonomy.genres.list`

### Metadata Sync (DataBridge internal)
`metadata.sync.upsert` — triggers Typesense document upsert after a successful ingestion.

### Schedules (WebAPI/Scheduler → DataBridge, request/reply)
`schedule.create`, `schedule.update`, `schedule.get`, `schedule.list`, `schedule.delete`, `schedule.mark-attempt`, `schedule.mark-success`, `schedule.mark-failure`

### Creator Discovery (WebAPI/Worker → DataBridge, request/reply)
`creator.create-source`, `creator.update-source`, `creator.get-source`, `creator.list-sources`, `creator.delete-source`, `creator.list-enabled-sources`, `creator.upsert-discovered-batch`, `creator.update-assets`

### Storage CRUD (WebAPI → DataBridge, request/reply)
`storage.local.create`, `storage.local.update`, `storage.network.create`, `storage.network.update`, `storage.object.s3-compatible.create`, `storage.object.s3-compatible.update`, `storage.object.azure-blob.create`, `storage.object.azure-blob.update`, `storage.object.google-cloud-storage.create`, `storage.object.google-cloud-storage.update`, `storage.list`, `storage.get`, `storage.delete`

### Option Presets (WebAPI → DataBridge, request/reply)
`preset.create`, `preset.update`, `preset.get`, `preset.list`, `preset.delete`

### Playlist Queries (WebAPI → DataBridge, request/reply)
`playlist.list`, `playlist.get`

### Orphan Cleanup
| Subject | Direction |
|---------|-----------|
| `orphan.worker.move-file` | DataBridge → Worker (req/reply) |
| `orphan.worker.delete-file` | DataBridge → Worker (req/reply) |
| `orphan.worker.restore-file` | DataBridge → Worker (req/reply) |
| `orphan.worker.file-exists` | DataBridge → Worker (req/reply) |
| `orphan.admin.list` | WebAPI → DataBridge (req/reply) |
| `orphan.admin.restore-file` | WebAPI → DataBridge (req/reply) |
| `orphan.admin.restore-metadata` | WebAPI → DataBridge (req/reply) |

### Filesystem Rescan (Worker ↔ DataBridge)
`fs.rescan.storage-keys` (DataBridge → Worker), `fs.rescan.reconcile` (Worker → DataBridge)

---

## 7. Cross-Cutting Infrastructure

### Idempotency & Deduplication
- JetStream `Nats-Msg-Id` header on all published messages (MessageId as hex).
- `processed_messages` table in DataBridge for at-least-once → effectively-once.
- Cleipnir uses `OperationKey` as a secondary deduplication key inside the saga.
- `DeterministicGuid.Create(baseId, suffix)` used by the Worker so result events have stable MessageIds even on JetStream redelivery.

### Failure Classification
Downloads can fail as `Transient` (retry), `Permanent` (don't retry), `Timeout`, or `Cancelled`. The saga retries transient failures up to 3 times, then transitions to `FailedTransient` or `FailedPermanent`.

### Secrets — OpenBAO
- Cookie files: `cookies/{key}` → field `content` (Netscape format).
- Storage credentials: stored per-backend with split-secret logic (`StorageSecretSplitter`).
- Accessed via `ISecretStore` / `OpenBaoSecretStore`.

### Storage Abstraction
`IBlobStorageProvider` wraps FluentStorage. `CachingBlobStorageProvider` adds a NATS-backed config cache so storage metadata doesn't require a DataBridge round-trip on every operation. Config changes propagate via `StorageConfigChangedSubscriber` (NATS publish/subscribe).

### Secrets in Storage
Storage credentials (passwords, access keys, tokens) are split by `StorageSecretSplitter` — the non-secret parameters go to Postgres, the sensitive parts go to OpenBAO at paths like `storage/{key}/credentials`.

---

## 8. Not Yet Implemented / Stub Areas

| Area | Status |
|------|--------|
| **MediaProcessor service** | Stub only — `Program.cs` with no consumers or logic. |
| **Download job status endpoint** | No HTTP endpoint to query a job's current state by `jobId`. Saga state is in DB but not exposed. |
| **Download progress endpoint** | `download.evt.download-progress` is published by the Worker but no consumer or HTTP endpoint surfaces it. |
| **Thumbnail download during ingestion** | `CapturedMediaMetadataCore.ThumbnailStoragePath` exists in the model but the ingestion flow does not download thumbnails — only creator source avatars/banners are downloaded. |
| **Content streaming / playback** | No `/stream` or `/serve` endpoint. `VideoServeLocalTest` is a helper testing app, not a production service. |
| **Authentication / authorization** | No auth middleware on any API endpoint. All endpoints are open. |
| **Webhook / notification on job completion** | No push notification mechanism when a download finishes. |
| **Download cancellation** | No endpoint or mechanism to cancel an in-flight download job. |
| **Discovered media → auto-download** | The `discovered_media` table tracks candidates, but no automatic dispatch of `DownloadRequested` for newly discovered entries is implemented. |
| **Playlist status polling endpoint** | Playlist state is in DB but no granular item-level status query is exposed beyond `GET /api/playlists/{id}`. |
| **Account avatar/banner write-back during ingestion** | `accounts` table has `avatar_storage_path` and `banner_storage_path` columns but the ingestion flow does not populate them (only creator source assets are refreshed). |
