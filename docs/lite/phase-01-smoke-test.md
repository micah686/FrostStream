# Phase 1 Full smoke-test procedure

This procedure creates the behavioral baseline later phases must preserve. Run it only against a disposable Full installation with synthetic credentials and non-production media.

## Fixture record

Before testing, record commit, Compose/env artifact hashes, image IDs, enabled auth mode, enabled chat/POT flags, public URLs, storage type/key, test-source URL or fixture identifier, source duration/size, and whether a physical casting device is available. Redact all credential values.

## Preconditions

1. The Full stack has empty disposable volumes, absolute media/backup paths, and `LIVE_CHAT_ENABLED=true`.
2. Compose configuration validation passes and all expected Full services become healthy. Record readiness time and container resource samples using `phase-01-baseline.md`.
3. Authentik has one test owner. OpenFGA provisioning completed. A local storage target and, where available, one credentialed non-local storage target exist.
4. The download fixture is legally usable, stable, short, and supports deterministic search/playback checks. Configure POT/cookies only when the selected provider needs them.

## Scenario

1. Open the frontend in a fresh browser profile. Confirm anonymous access redirects to login, authenticate through Authentik, return to FrostStream, and verify `/api/auth/me` identifies the test owner.
2. Create or select a storage target. If credentialed storage is included, save credentials and verify they never appear in browser responses or application logs.
3. Open the download queue SSE stream, submit the fixture through the normal UI/API, and record the returned job/correlation identifiers.
4. Confirm the Worker registers/heartbeats, claims the intended worker tag, materializes cookies if configured, invokes POT if configured, and emits progress. Exercise stop/start or cancel/retry once and confirm the persisted queue reflects it.
5. Wait for yt-dlp completion and DataBridge ingestion. Verify catalog identity, source version, storage key, content hash, sidecars/assets, and absence of duplicate catalog commits.
6. Confirm MediaProcessor creates the expected thumbnail and applicable rendition. Verify rendition progress/status reaches the UI.
7. Search for a unique fixture term. Open the item, request a byte range, start browser playback, seek, load captions if present, and verify watch state persists after reload.
8. If the fixture contains archived live chat, ingest it into ClickHouse and replay a known time window. Otherwise record chat as fixture-unavailable rather than passing it implicitly.
9. Cast to a physical FCast/Chromecast-compatible target using the configured advertised base URL. Verify the target fetches and plays the media. If hardware is unavailable, keep this check unverified.
10. Trigger a manual backup through the supported Full API/UI, poll to terminal success, list the repository entry, and record backup duration and size delta. Backup success is only a baseline here; Phase 17 proves restoration.
11. Stop/recreate the application containers without removing volumes. Confirm login, catalog record, search result, playback/watch state, and backup listing survive.

## Required evidence

- Redacted HTTP status/payload summaries for login identity, submission, queue terminal state, catalog lookup, search, media range response, watch state, and backup job.
- Container health/restart table, timing measurements, and three idle resource samples.
- Job/correlation/media IDs and storage-relative paths sufficient to correlate logs and PostgreSQL state without exposing secrets.
- Test pass/fail/unverified status for login, worker acquisition, ingest, processing, search, playback, SSE, retry/cancel, chat, casting, credentialed storage, backup, and restart persistence.
- Any failure classified as code regression, pre-existing defect, environment/prerequisite failure, or unavailable optional hardware/provider.

Do not mark the Full smoke test passed when a required step was skipped. A controlled local source can verify orchestration, but a separate provider/POT check remains necessary where YouTube functionality is claimed.

