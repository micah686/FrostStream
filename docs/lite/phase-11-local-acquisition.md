# Phase 11 — local acquisition evidence

Date: 2026-09-13

State: Verified, awaiting acceptance. This remains a development milestone, not a deployable Lite edition.

## Delivered behavior

Lite exposes durable direct ingress for downloads, provider playlists, creator scans, and reviewed
incoming-file imports. Each ingress commits the existing PostgreSQL job/session intent before adding a
deduplicated `jobs.local_execution_work` row and returning `202 Accepted`. Four local handlers execute
that work under Phase 10's exclusive database owner; no Worker, DataBridge NATS consumer, remote lease,
worker tag, or queue group is activated.

The download handler resolves option presets, validates the selected storage target, materializes an
optional fixed-owner cookie profile, performs yt-dlp metadata/acquisition, and keeps request fields for
video/audio selection, comments, tags, priority, force, source kind, and future rendition work. It
reserves the normal content-ID edition, writes the primary, info JSON, thumbnails/captions when emitted,
and a recovery `.meta` sidecar, records `jobs.download_artifacts`, maps rich metadata, links playlist
membership, and settles the normal run. Storage object reuse requires matching length and xxHash128;
an existing partial/corrupt object is overwritten. Stable per-work acquisition paths and checkpoints,
content-version reservation, and object verification make retries converge without duplicate editions.

Migration 100 adds monotonic progress sequence, percentage, and message fields to the local ledger and
raises initialization compatibility to version 3. Polling and snapshot-first SSE expose the same
persisted fields. Terminal events reload the row after completion so their progress agrees with the
authoritative snapshot. Explicit user cancellation settles the download/import state, while host or
ownership cancellation leaves active intent recoverable by the next process.

`/api/creators` provides direct fixed-owner subscription CRUD. Creator scan work resolves a subscription's
download config set, performs bounded flat yt-dlp discovery, persists candidates/checkpoints, and only
fans out durable downloads when requested. Playlist expansion persists provider order and creates one
normal run and local download item per entry. Import scan is restricted to the configured incoming root;
session/item reads and bulk review actions precede commit, after which hashing, reservation, verified
storage, and terminal session counters execute locally.

Optional POT support uses `LitePotProvider` and an in-process `/api/internal/pot/**` proxy to health-check
and forward directly to the configured bgutil HTTP provider. The yt-dlp plugin/base URL is applied only
when enabled. Disabled, available, and unavailable behavior is covered without registering the Full
NATS POT broker.

## Changed files

- `src/App/DataBridge/Lite/LiteAcquisitionModels.cs`, `LiteAcquisitionServiceCollectionExtensions.cs`,
  `LiteDownloadAcquisition.cs`, `LiteDiscoveryAndImport.cs`, `LiteAcquisitionRecoveryService.cs`,
  `LiteArtifactReconciler.cs`, `LiteCookieMaterializer.cs`, and `LitePotProvider.cs`
- `src/App/DataBridge/Lite/LocalExecutionModels.cs`, `NpgsqlLocalExecutionStore.cs`, and
  `LocalExecutionDispatcher.cs`
- `src/App/DataBridge/Migrations/FluentMigrator/100_AddLocalExecutionProgress.cs` and
  `src/App/DataBridge/Initialization/ApplicationInitializationCoordinator.cs`
- `src/App/FrostStream.Lite/Program.cs`, `appsettings.json`, capability reporting, and the download,
  creator, discovery/import, POT proxy, and per-work local-job controllers
- `Tests/UnitTests/Lite/LiteAcquisitionTests.cs`, `LiteExecutionPostgresTests.cs`, and `LiteHostTests.cs`
- `FROSTSTREAM_LITE_PHASES.MD`, this evidence record, and the Phase 1 transport inventory

## Automated verification

Commands run from the repository root:

```bash
dotnet build src/App/FrostStream.slnx --no-restore --nologo -m:1

dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LiteAcquisitionTests/*' --no-ansi --disable-logo
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LiteHostTests/*' --no-ansi --disable-logo
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LocalExecutionDispatcherTests/*' --no-ansi --disable-logo

FROSTSTREAM_TEST_POSTGRES='Host=127.0.0.1;Port=35511;Database=froststream_phase11_tests;Username=postgres;Password=synthetic' \
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LiteExecutionPostgresTests/*' --no-ansi --disable-logo

dotnet run --no-build --no-restore --project Tests/UnitTests/UnitTests.csproj -- \
  --no-ansi --disable-logo
dotnet run --no-build --no-restore \
  --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- \
  --no-ansi --disable-logo
git diff --check
```

Results:

- Lite acquisition/POT/reconciliation tests: 6/6 passed, including disabled, enabled direct
  health/forward, actionable unavailable-provider, and partial-object cases;
- Lite composition tests: 7/7 passed and register exactly four acquisition handlers without forbidden
  NATS/auth/OpenBao services;
- controlled dispatcher tests: 5/5 passed;
- real PostgreSQL ownership/ledger/progress tests: 2/2 passed;
- solution build: 0 warnings, 0 errors;
- Full integration regression suite: 85/85 passed;
- complete unit suite: 518/522 passed. The same four unrelated failures recorded before Phase 11 remain:
  `CreatePolicy_Returns_Accepted_When_OpenFga_Synchronization_Is_Deferred`,
  `Every_Controller_Endpoint_Has_Detailed_OpenApi_Metadata`,
  `Authentication_Challenge_Is_Reported_As_Permanent_With_Specific_Code`, and
  `Map_Uses_Per_Comment_Unknown_Account_Handle_When_Comment_Author_Is_Missing`.

## Live controlled-fixture evidence

Disposable PostgreSQL 17 and Typesense 30.2 containers were initialized with synthetic credentials.
`DataBridge initialize` applied migration 100 and recorded compatibility version 3. A four-second,
52,004-byte MP4 and a local HTML page containing two ordered HTML5 video entries were served on
loopback; yt-dlp 2026.08.19 and the configured ffmpeg/ffprobe binaries performed the real acquisition.

Observed outcomes:

- a direct download reached `Completed`, attempt 1, progress sequence 2000/100%, produced one content
  version and rich metadata row, and stored byte-identical `media.mp4`, `media.info.json`, and `.meta`;
- the artifact ledger contained three `stored` rows (`primary`, `info-json`, `meta`); a forced replay
  reused the content edition, leaving the version count unchanged;
- truncating the disposable stored primary to 10 bytes and replaying repaired it to 52,004 bytes with
  the exact fixture xxHash128 while still leaving the edition count unchanged;
- an unavailable storage key retried automatically through attempt 3 and ended `Failed` with
  `acquisition_failed`, retaining its persisted validation progress;
- a throttled 200 MiB transfer exposed live persisted progress, accepted cancellation at 5.3%, and
  ended `Cancelled`; the normal download job ended `stopped/stopped`;
- interrupting a throttled transfer left ledger/job state `running`. Restart reclaimed the same work ID
  at attempt 2 and ended both ledger and normal job `Completed` without manual repair;
- an encrypted cookie profile completed a cookie-backed acquisition; no `cookies-*` scratch file
  remained and the synthetic plaintext was absent from the persisted ciphertext directory;
- import scan found one file, bulk include enabled it, commit produced one durable item, and the session
  ended `Completed` with one imported item linked to the existing content edition;
- creator subscription create/list succeeded. The controlled full scan persisted two candidates and
  reported `Discovered 2 item(s); queued 0` when enqueue was disabled;
- playlist expansion persisted indexes 1 and 2 in source order and both child download ledger items
  completed on attempt 1.

The temporary HTTP servers were stopped, and the disposable PostgreSQL/Typesense containers were
stopped and auto-removed after verification.

## Reproduction outline

1. Start disposable PostgreSQL and Typesense, set an absolute `FROSTSTREAM_STORAGE_ROOT`, and run
   `DataBridge initialize` with those connection settings.
2. Configure `LiteAcquisition:YtDlpPath`, `FfmpegPath`, `FfprobePath`, `TempPath`, and `IncomingPath`;
   start `FrostStream.Lite` against the initialized services.
3. Submit `POST /api/downloads`, then poll `GET /api/jobs/local/{workId}` or subscribe to
   `/api/jobs/local/{workId}/stream`. Expect persisted progress and a terminal catalog-backed result.
4. For imports, call `/api/imports/scan`, review `/api/imports/{sessionId}/items`, apply an `Include` or
   `AcceptPlaceholders` bulk action, then call `/api/imports/{sessionId}/commit`.
5. Create a subscription with `POST /api/creators`, scan it with `/api/creators/scan`, or submit a
   provider playlist to `/api/playlists/expand`.
6. For POT, set `LitePot:Enabled=true`, an absolute healthy `ProviderUrl`, a Lite-reachable
   `ProxyBaseUrl`, and the plugin directory. An unavailable `/ping` must fail work with the documented
   configuration/disable guidance.

## Known limitations and external prerequisites

- The controlled POT test uses an HTTP stub. A real YouTube/bgutil acceptance run requires the matching
  yt-dlp POT plugin and an operator-provided compatible provider; its URL and image are deployment work
  in later phases.
- Creator avatar/banner acquisition and derived media renditions remain Phase 12 work. Phase 11 retains
  the requested rendition flag and completes primary media editions but does not claim derived output.
- Import scan/selection/commit is implemented; optional probe, mapping-file upload, and yt-dlp import
  enrichment UI parity remain outside this phase's controlled local-import gate.
- Progress events are advisory and bounded. The PostgreSQL snapshot is authoritative after reconnect.
- Runtime configuration/deployment packaging is not complete until later Lite phases; this milestone is
  intentionally not advertised as a deployable edition.

## Rollback

Stop Lite first. Remove the Phase 11 acquisition registrations, controllers, handlers, options, tests,
and migration 100; restore capability flags and initialization compatibility version 2. If migration
100 was applied, remove only `progress_sequence`, `progress_percent`, and `progress_message` from
`jobs.local_execution_work` and remove migration version 100 from FluentMigrator `VersionInfo` before
running the older code. Existing catalog media/artifact rows and stored objects are normal user data;
do not delete them as part of code rollback. Full composition and its NATS/OpenBao paths remain intact.
