# Channel Avatar / Banner Asset Cache

## Context

The recent channel-discovery work (commit `b5470e4`) added `CreatorSourceEntity`/`DiscoveredMediaEntity` and a `ChannelDiscoveryConsumerService` that scans channels for new media. The Scheduler already publishes `ChannelAssetRefreshRequested` to `fs.channel.asset-refresh.request` on a cron via `ChannelAssetRefreshJob` + `ChannelAssetRefresher` — **but nothing in Worker or DataBridge consumes it**, so channel-level avatar/banner art is never fetched.

This plan adds the missing consumer: a Worker background service that resolves channel-level thumbnails from yt-dlp, downloads them to a local content-addressable cache, and records URLs/paths/hashes on the `creator_sources` row. The cache must scale to hundreds of thousands of avatars (so two-level xxhash128 sharding). A WebAPI endpoint lets operators force a redownload for a single source.

**Stays separate from video download flow.** Channel assets are per-CreatorSource (not per-video), change rarely, and have their own scheduled cadence. Coupling them to ingestion would re-fetch unchanged art on every video.

## Architecture

```
Scheduler                           Worker                              DataBridge
─────────                           ──────                              ──────────
ChannelAssetRefreshJob              ChannelAssetRefreshConsumerService  CreatorDiscoveryRepository
  └─ ChannelAssetRefresher              ├─ list enabled sources (NATS RPC) ──→ ListEnabledSourcesForScan
       publishes →→→→→→→→→→→→→→→→→→ subscribes fs.channel.asset-refresh.request
                                        ├─ for each source:
                                        │    ├─ yt-dlp non-flat on SourceUrl  (IYtDlpClient.GetVideoInfoAsync)
                                        │    ├─ pick avatar+banner Thumbnails
                                        │    ├─ AssetCacheWriter.DownloadAndStoreAsync()
                                        │    │    ├─ HttpClient GET, 3 tries, expo backoff
                                        │    │    ├─ XxHash128 → 2-level shard path
                                        │    │    └─ atomic write (tmp + rename)
                                        │    └─ publish UpdateAssets(source) ──→ UpdateAssetsAsync (new repo method)

WebAPI
──────
POST /api/creator-sources/{id}/refresh-assets?force=true
  └─ publishes ChannelAssetRefreshRequested { TargetSourceId = id, Force = true }
```

## Files to Modify / Add

### New files

- `src/App/DataBridge/Migrations/FluentMigrator/014_AddCreatorSourceAssetColumns.cs`
- `src/App/Worker/Services/ChannelAssetRefreshConsumerService.cs` — `BackgroundService`, mirrors `ChannelDiscoveryConsumerService` shape
- `src/App/Worker/Services/AssetCacheWriter.cs` — pure download + write logic; testable in isolation
- `src/App/Worker/Services/AssetCacheOptions.cs` — `Root` (string), `MaxAttempts` (default 3), `InitialBackoff` (default 250ms), `RequestTimeout` (default 30s), `FreshnessWindow` (default 7d)
- `src/App/WebAPI/Endpoints/CreatorSourceAssetsEndpoint.cs` (or extend existing creator-source endpoints if present — check `src/App/WebAPI/Endpoints/` first)

### Modified files

- `src/App/Shared/Database/CreatorDiscoveryEntities.cs` — add asset columns to `CreatorSourceEntity`
- `src/App/Shared/Messaging/BackgroundJobMessages.cs` — extend `ChannelAssetRefreshRequested` with `long? TargetSourceId` (null = all enabled) and `bool Force = false`
- `src/App/Shared/Messaging/BackgroundJobsTopology.cs` — add `WorkerChannelAssetRefreshConsumer` ConsumerSpec (AckWait 30min, MaxDeliver 3, queue group `worker-bgjobs`)
- `src/App/Shared/Messaging/CreatorDiscoveryMessages.cs` — add `UpdateCreatorSourceAssetsRequestMessage` { SourceId, AvatarUrl, AvatarCachePath, AvatarContentHash, BannerUrl, BannerCachePath, BannerContentHash, RefreshedAt, AttemptedAt, AttemptCount, LastError } and response
- `src/App/Shared/Messaging/CreatorDiscoverySubjects.cs` — add `UpdateAssets = "fs.creator-source.assets.update"`
- `src/App/DataBridge/Data/CreatorDiscoveryRepository.cs` — implement `UpdateAssetsAsync(UpdateCreatorSourceAssetsRequestMessage)`
- `src/App/DataBridge/Data/ICreatorDiscoveryRepository.cs` — declare it
- `src/App/DataBridge/Services/...` — wire NATS subscription for `UpdateAssets` (mirror the existing CRUD subscriptions in this file)
- `src/App/Worker/Program.cs` — register `IHttpClientFactory` named `"asset-cache"` with timeout from options; `AddOptions<AssetCacheOptions>().Bind("Cache:Assets")`; `AddHostedService<ChannelAssetRefreshConsumerService>()`; `AddSingleton<AssetCacheWriter>()`
- `src/App/Worker/appsettings.json` — `"Cache": { "Assets": { "Root": ".cache" } }` (resolved against `ContentRoot` at startup if relative)
- `src/App/Scheduler/ChannelTasks/ChannelAssetRefresher.cs` — preserve existing behavior (no TargetSourceId, no Force); WebAPI populates these on its direct publish

## DB Migration `014_AddCreatorSourceAssetColumns.cs`

Add to `creator_sources`:

| Column | Type | Null |
|---|---|---|
| `avatar_url` | string(4096) | yes |
| `avatar_cache_path` | string(1024) | yes |
| `avatar_content_hash` | string(64) | yes |
| `banner_url` | string(4096) | yes |
| `banner_cache_path` | string(1024) | yes |
| `banner_content_hash` | string(64) | yes |
| `assets_last_refreshed_at` | timestamptz | yes |
| `assets_last_attempt_at` | timestamptz | yes |
| `assets_attempt_count` | int32 | no, default 0 |
| `assets_last_error` | string(2048) | yes |

Reverse direction drops the same columns. Snake_case naming consistent with existing migrations.

## yt-dlp Asset Extraction

In `ChannelAssetRefreshConsumerService`:

1. Call `IYtDlpClient.GetVideoInfoAsync(source.SourceUrl, options: new YtDlpVideoSelectionOptions { Flat = false, PlaylistItems = "0" }, ct)` — `playlist_items=0` causes yt-dlp to return only top-level (channel) metadata, no video entries; far cheaper than scanning the whole channel.
2. From the returned `VideoInfo.Thumbnails`, pick:
   - **Avatar**: first thumbnail whose `Id` matches `^avatar` (case-insensitive), preferring `avatar_uncropped`. Fallback: thumbnail with `Width ≈ Height` (aspect 0.9–1.1) and largest `Preference`/`Width`.
   - **Banner**: first thumbnail whose `Id` matches `^banner`, preferring `banner_uncropped`. Fallback: thumbnail with aspect `Width/Height ≥ 3.0` (channel banners are very wide) and largest `Preference`/`Width`.
3. If a kind isn't found, skip it for this source (don't fail the whole refresh).

Helper for selection lives in the consumer file — small enough not to need its own class.

## AssetCacheWriter

Pure class, no DI besides `IHttpClientFactory`, `ILogger`, `IOptions<AssetCacheOptions>`:

```
DownloadAndStoreAsync(string url, AssetKind kind, CancellationToken)
  → DownloadResult { CachePath, ContentHash, ContentLength, Extension, Attempts }
```

- Loop up to `options.MaxAttempts` (default 3) with exponential backoff (250ms → 500ms → 1s); same shape as `ScheduleHydrationService.RequestWithRetryAsync` at `src/App/Scheduler/Services/ScheduleHydrationService.cs:76` — copy that loop inline rather than introducing Polly.
- Download to a temp file under `{root}/.tmp/{guid}`, hashing `XxHash128` while streaming (same usage as `DownloadCommandsConsumerService.ComputeXxHash128Async`).
- Resolve extension: prefer URL path suffix; fall back to `Content-Type` mapping (`image/jpeg`→`.jpg`, `image/png`→`.png`, `image/webp`→`.webp`); final fallback `.bin`.
- Final path: `{root}/{kind}s/{hash[0..2]}/{hash[2..4]}/{hash}{ext}` — kind is `avatar` or `banner` (pluralized to match the recommended preview).
- If the final path already exists with the same hash → skip rewrite, return existing result.
- Atomic move: temp → final via `File.Move(tmp, final, overwrite: false)`; if `File.Exists(final)`, delete temp.
- Throws after final attempt; consumer catches and records `LastError`.

## Worker Consumer Flow

For each `ChannelAssetRefreshRequested`:

1. Look up sources via existing NATS RPC:
   - If `TargetSourceId` set → `CreatorSourceGetRequestMessage`.
   - Else → `CreatorSourceListEnabledForScanRequestMessage`.
2. For each source, if `!Force` and `assets_last_refreshed_at` is within `FreshnessWindow` (default 7 days) → skip.
3. Resolve thumbnails via yt-dlp.
4. For each kind present, call `AssetCacheWriter.DownloadAndStoreAsync`. Track per-kind result.
5. Publish `UpdateCreatorSourceAssetsRequestMessage` to `fs.creator-source.assets.update`:
   - On total success: set URLs, paths, hashes, `RefreshedAt = now`, `AttemptCount = 0`, `LastError = null`.
   - On partial/total failure: set `AttemptedAt = now`, `AttemptCount += 1`, `LastError = message`; leave existing URL/path/hash fields untouched (so stale-but-usable art survives a transient failure).

JetStream `MaxDeliver = 3` provides one redelivery layer above the per-asset 3 attempts; this is intentional and parallels existing consumers.

## WebAPI Endpoint

`POST /api/creator-sources/{id:long}/refresh-assets?force=true`

- Verify source exists (NATS RPC `CreatorSourceGet`); 404 if missing.
- Publish `ChannelAssetRefreshRequested { TargetSourceId = id, Force = force, ScheduleKey = "manual", TaskType = TaskTypeRegistry.ChannelAssetRefresh, IdempotencyKey = $"manual:{id}:{Guid.NewGuid()}", DueWindowUtc = now, OccurredAt = now }`.
- Return `202 Accepted` with `{ "queued": true, "sourceId": id }`.
- Check `src/App/WebAPI/Endpoints/` for existing `CreatorSource` endpoints first — extend the same file if it exists.

## Verification

1. **Build**: `dotnet build src/FrostStream.sln` — must pass with `TreatWarningsAsErrors`.
2. **Migration**: launch AppHost; confirm migration 014 runs and columns appear (`\d creator_sources` in the Postgres container).
3. **Scheduled path**: temporarily lower the cron for `channel_asset_refresh` in the seeded schedules, or trigger via the Quartz dashboard; verify the Worker logs the consumer firing and that `.cache/avatars/...` / `.cache/banners/...` files appear.
4. **Force path**: with at least one enabled `creator_sources` row (YouTube channel URL), `curl -X POST http://localhost:{port}/api/creator-sources/{id}/refresh-assets?force=true`; verify the same flow runs ignoring freshness and overwrites only when the upstream hash changes.
5. **Retry path**: point a source's url at an unreachable host, observe 3 attempts + backoff in logs, then `assets_last_error` populated and `assets_attempt_count` incremented while `avatar_url`/`banner_url` remain at last good values (or null if never fetched).
6. **Dedup**: trigger force refresh twice in a row on a source whose art hasn't changed upstream — second run should report the existing-hash short-circuit in logs and not rewrite the file.
7. **Topology**: confirm `nats consumer ls FROSTSTREAM_BACKGROUND` lists `worker-channel-asset-refresh`.

## Out of scope

- Cache eviction / GC of orphaned files (separate maintenance job; the existing `StaleDatabaseCleanupJob` is the natural home for a later follow-up).
- Serving assets through WebAPI (the cache paths are stored; serving can be added when a UI consumes them).
- Non-YouTube extractors that surface assets differently — the ID-based picker degrades gracefully (returns nothing) and the aspect-ratio fallbacks cover most cases.
