# Phase 6 shared application operations

Status: accepted on 2026-09-11 by the request to proceed with Phase 7.

Verified: 2026-09-11 on `feature/lite-implementation`. Phase 5 is accepted by the request to proceed
with Phase 6. No credential, browser cookie, recovery material, or note content from the verification
installation is retained in source control.

## Result

Synchronous DataBridge operations now have a reusable registration module that can be consumed
without invoking an executable entry point. User notes are the reference complete vertical path:

```text
Lite/future HTTP -> IUserNoteApplication -> UserNoteApplication -> IUserNotesRepository
Full HTTP        -> IUserNoteApplication -> NatsUserNoteApplication -> NATS
NATS             -> UserNoteConsumerService -> UserNoteApplication -> IUserNotesRepository
```

`UserNoteApplication` contains validation, repository calls, DTO mapping, cancellation, logging, and
the existing `validation`, `not_found`, `target_not_found`, and `internal_error` shapes. The Full
subscriber contains only subject registration, scoped service resolution, and replies. WebAPI keeps
the existing 10-second unavailable behavior in its NATS proxy, while its controller is transport-free.

`ICurrentOwner` separates identity selection from endpoints. `HttpCurrentOwner` reads the authenticated
Full claim. `FixedCurrentOwner` returns `AuthConstants.SingleUserSubject` for the future Lite composition
root. The controller never accepts an owner supplied by the caller.

## Synchronous operation inventory

| Area | Typed direct seam registered by the module | Full transport retained |
| --- | --- | --- |
| Catalog metadata | `IMetadataRepository`, `IMetadataReadService` | metadata/search request-reply adapters |
| Library/statistics | `IStatisticsReadService`, `IPlaylistsRepository`, `ICreatorDiscoveryRepository` | query and discovery request-reply adapters |
| Storage/media resolution | `IMediaStreamReadService`, `IMediaThumbnailReadService`, `IMediaCaptionReadService`, `IAccountAssetReadService` | stream and storage CRUD adapters; secret-backed CRUD stays Full until Phase 9 selects a local `ISecretStore` |
| Preferences/configuration | `IOptionPresetsRepository`, `IDownloadConfigSetsRepository` | option/config request-reply adapters |
| Playlists | `IPlaylistsRepository`, `IUserPlaylistsRepository` | provider and owner-scoped playlist adapters; durable expansion belongs to Phase 7 |
| Watch/rendition state | `IAudioRenditionRepository`, `IMediaEncodingStatusRepository`, `IStreamRenditionRepository`, `IRenditionQueueRepository` | watch/like and rendition adapters; processing belongs to Phase 7 |
| Notes | `IUserNoteApplication`, `IUserNotesRepository` | `NatsUserNoteApplication` and `UserNoteConsumerService` |
| Notifications | existing scoped database/dispatcher operations remain registered by their Full composition; transport delivery remains a Full adapter | notification request/reply and dispatch subjects; local secrets belong to Phase 9 |
| Cookie profiles | existing scoped database and `ISecretStore` operations remain selected by the composition root | cookie request/reply subjects; encrypted local secrets belong to Phase 9 |
| Related synchronous operations | `IImportSessionRepository`, `IScheduledTasksRepository`, download query repositories, thumbnail generation services | durable execution/change signals remain assigned to Phases 7, 10, and 13 |

The reusable module intentionally does not register NATS, hosted consumers, workflow executors, or
`Program.Main`. Repository and database-backed services are scoped, so every direct HTTP operation gets
the same database lifetime it received through a Full subscriber scope.

## Verification

Build:

```bash
dotnet build src/App/FrostStream.slnx --no-restore -v:minimal
```

Result: succeeded with zero warnings and errors.

Focused tests:

```bash
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/*/*UserNote*/*' --progress off --no-ansi

dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/*/DataBridgeApplicationRegistrationTests/*' --progress off --no-ansi
```

Results: 13/13 user-note controller/direct/Full-adapter tests passed; 3/3 reusable-composition and
fixed/Full-owner tests passed. The parity cases cover success, blank/missing-owner validation, owner and
cancellation propagation, repository failure, NATS transport failure, and the public idempotent-delete
mapping.

The complete unit run executed 480 tests: 476 passed and the same four unrelated assertions already
present on the branch failed (`AccessControlControllerTests`, `EndpointMetadataTests`,
`YtDlpFailureDetailsTests`, and `YtDlpMetadataMapperTests`). The formerly stale user-note delete test was
aligned with the controller's documented 204 idempotency contract.

Integration:

```bash
dotnet test --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj \
  --no-build --no-restore
```

Result: 84/84 passed in 44 seconds. The first run completed all 84 test bodies but exposed a concurrent
host-shutdown race. `SubscriptionBackgroundService` now snapshots and clears subscriptions under a lock,
and `RenditionProgressHub` atomically takes its subscription before stopping it. The rerun exited cleanly.

Full smoke used the preserved isolated `froststream-phase5-full` installation. DataBridge and WebAPI
images were rebuilt, application containers were recreated without deleting volumes, and `/health`
returned 200. A temporary Playwright check logged into synthetic Authentik as `akadmin`, then exercised
an existing video through WebAPI -> NATS -> DataBridge:

```text
/api/auth/me                                      200
GET    /api/user/notes/video/{existing-media-id}  200
PUT    /api/user/notes/video/{existing-media-id}  200
GET    /api/user/notes/video/{existing-media-id}  200, exact note round trip
DELETE /api/user/notes/video/{existing-media-id}  204
```

The temporary note was deleted. The smoke did not alter deployment inputs or persistent infrastructure.

## Manual reproduction

1. Start an initialized Full runtime and log in through its normal Authentik flow.
2. Obtain `/api/auth/csrf`; send its token in `X-CSRF-TOKEN` for mutations.
3. PUT `{"note":"phase-6-check"}` to `/api/user/notes/video/{an-existing-media-guid}`.
4. GET the same route and expect 200 with the same note. DELETE it and expect 204.
5. Stop NATS or DataBridge and repeat GET; expect the existing 503 unavailable response rather than an
   exception or changed error body.

For the direct path, construct a service collection, add database/options dependencies, call
`AddDataBridgeApplicationOperations()`, create a scope, and resolve `IUserNoteApplication`. Do not call
`DataBridge.Program.Main`.

## Known limits and later ownership

This phase does not merge hosts, enable Lite routes, replace secret storage, or localize durable work.
Those remain Phases 7 through 10. Full retains NATS by design. Physical casting and provider-backed
storage are unchanged and were not re-run because this phase changes neither path.

## Rollback

Revert the shared application/current-owner contracts, restore the user-note logic to its NATS consumer
and controller, and restore the inline DataBridge service registrations. No database migration or data
rollback is required. If rolling back the shutdown-race fix, expect the prior nondeterministic aggregate
exception during parallel integration-host disposal. Preserve all Full volumes; do not use `down -v`.
