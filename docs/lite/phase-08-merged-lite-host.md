# Phase 8 — merged Lite host evidence

Date: 2026-09-12

State: Verified, awaiting acceptance. This remains a development milestone, not a deployable Lite edition.

## Delivered boundary

`FrostStream.Lite` is a single ASP.NET Core composition root. It discovers only controllers in its
own assembly and directly registers the Phase 8 DataBridge subset: relational catalog detail/random
reads, fixed-owner notes, and option-preset preferences. It does not invoke another executable entry
point and registers no NATS transport, OIDC/JWT/cookie authentication, session ticket store,
OpenFGA/OpenBao client, user provisioning, worker, workflow executor, scheduler, or backup runtime.

The default command is `run`. `initialize`, `backup`, and `restore` are recognized command boundaries
and fail explicitly as reserved until their owning phases connect them; unknown commands also fail
before host construction.

The public Phase 8 routes are:

- `GET /api/system/capabilities`
- `GET /api/auth/config` and sessionless `GET /api/auth/me`
- `GET /api/metadata/random` and `GET /api/metadata/{mediaGuid}`
- `GET|PUT|DELETE /api/user/notes/...` plus note listing
- option-preset `GET|POST|PUT|DELETE /api/user/option-presets/...`
- `GET /health` and `GET /alive`

Capabilities which require local dispatch, processing, search/chat, casting, schedules, backups,
multi-user management, or remote workers report `false`. This prevents the Phase 8 backend from
claiming later-phase readiness.

All user-note calls receive `AuthConstants.SingleUserSubject` through `FixedCurrentOwner`. There is no
authentication middleware and `/api/auth/me` creates no session; its compatibility response reports
`authenticated: false` and the stable owner subject. Option presets retain their existing global
schema semantics.

Production validation requires PostgreSQL, Typesense URL/key, and the persistent storage root. It
does not require Authentik, OIDC, OpenFGA, OpenBao, NATS, or internal media-processor credentials.
Full's WebAPI composition and hardening validation are unchanged.

## Changed files

- `src/App/FrostStream.Lite/` — host, controllers, command parser, validation, health/composition
  guards, Dockerfile, and baseline configuration.
- `src/App/DataBridge/Lite/DataBridgeLiteServiceCollectionExtensions.cs` — transport-free Phase 8
  persistence/application registration.
- `src/App/FrostStream.slnx` — includes the Lite project.
- `Tests/UnitTests/Lite/LiteHostTests.cs` and `Tests/UnitTests/UnitTests.csproj` — focused composition,
  command, production-validation, fixed-owner, and session compatibility coverage.
- `FROSTSTREAM_LITE_PHASES.MD` — Phase 7 acceptance and Phase 8 evidence state.

## Automated verification

Commands:

```bash
dotnet restore src/App/FrostStream.Lite/FrostStream.Lite.csproj
dotnet build src/App/FrostStream.Lite/FrostStream.Lite.csproj --no-restore
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LiteHostTests/*'
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.WebAPI/WebApiHardeningTests/*'
dotnet build src/App/FrostStream.slnx --no-restore -m:1
dotnet test --project Tests/UnitTests/UnitTests.csproj --no-restore
git diff --check
```

Results:

- Lite project build: passed, 0 warnings, 0 errors.
- Phase 8 focused tests: 7/7 passed.
- unchanged Full WebAPI hardening tests: 12/12 passed.
- complete solution build: passed, 0 warnings, 0 errors.
- complete unit suite: 490/494 passed. The four failures predate and do not intersect this phase:
  `CreatePolicy_Returns_Accepted_When_OpenFga_Synchronization_Is_Deferred`,
  `Every_Controller_Endpoint_Has_Detailed_OpenApi_Metadata`,
  `Authentication_Challenge_Is_Reported_As_Permanent_With_Specific_Code`, and
  `Map_Uses_Per_Comment_Unknown_Account_Handle_When_Comment_Author_Is_Missing`.
- whitespace/error check: passed.

## Runtime verification

The existing disposable Phase 5 PostgreSQL fixture contained application initialization version 1.
A synthetic login was created for the smoke and removed afterward; the synthetic option preset was
also deleted, the Lite process stopped gracefully, and the fixture was returned to its prior stopped
state.

Launch shape (replace the synthetic connection values with an initialized development database):

```bash
DOTNET_ENVIRONMENT=Development \
ConnectionStrings__froststreamdb='Host=127.0.0.1;Port=5432;Database=froststreamdb;Username=phase8_lite_test;Password=synthetic' \
dotnet run --no-build --no-restore \
  --project src/App/FrostStream.Lite/FrostStream.Lite.csproj -- \
  --urls http://127.0.0.1:5098
```

Smoke commands:

```bash
curl -fsS http://127.0.0.1:5098/health
curl -fsS http://127.0.0.1:5098/api/system/capabilities
curl -fsS -H 'Cookie: arbitrary=session-a' http://127.0.0.1:5098/api/auth/me
curl -fsS -H 'Cookie: arbitrary=session-b' http://127.0.0.1:5098/api/auth/me
curl -fsS http://127.0.0.1:5098/api/metadata/random
curl -fsS http://127.0.0.1:5098/api/user/notes
curl -fsS -X POST -H 'Content-Type: application/json' \
  --data '{"key":"phase8-smoke","name":"Phase 8 Smoke","ytDlpOptions":{}}' \
  http://127.0.0.1:5098/api/user/option-presets
curl -fsS -X DELETE http://127.0.0.1:5098/api/user/option-presets/phase8-smoke
```

Observed results: health was `Healthy`; capabilities identified `lite`; both cookie variants returned
the same `single-user-owner` with `authenticated: false`; catalog returned an existing media GUID;
notes returned a successful empty fixed-owner page; and preference create/delete returned 201/204.
No identity provider or broker was running or contacted.

## Known limitations

- This phase deliberately exposes only the routes backed by implemented local operations. The
  frontend, media delivery, search/chat, casting, jobs, schedules, backup, and processing routes are
  connected in later phases.
- `/health` currently verifies the PostgreSQL application initialization marker. Typesense and
  ClickHouse readiness join the merged application when their local modules are enabled.
- The Lite AppHost/Compose production graph remains unavailable; deployment wiring is a later gate.
- The reserved lifecycle commands do not perform work yet and fail explicitly.
- Phase 9 provides encrypted local secrets, so credentialed storage and cookie routes are not exposed.

## Rollback

Remove `src/App/FrostStream.Lite/`, `src/App/DataBridge/Lite/`, and `Tests/UnitTests/Lite/`; remove the
two project references from `src/App/FrostStream.slnx` and `Tests/UnitTests/UnitTests.csproj`; then
revert the Phase 7/8 status edits in `FROSTSTREAM_LITE_PHASES.MD`. No migration, durable schema,
Full runtime registration, generated deployment output, or persistent application data was changed.
