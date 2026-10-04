# Lite runtime verification

From the repository root:

```sh
dotnet run --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- \
  --treenode-filter '/*/IntegrationTests.Lite/*/*'
```

Requirements: .NET 10, a Docker-compatible container engine, and `ffmpeg`/`ffprobe` on PATH. For rootless Podman, set `DOCKER_HOST` to its socket and `TESTCONTAINERS_RYUK_DISABLED=true`; the fixtures explicitly dispose their containers. The suite creates a disposable Typesense 30.1 container for each test. It does not start NATS, PostgreSQL, OpenBao, Authentik, OpenFGA, or a backup service for Lite.

The fixture runs the production Lite composition on a real loopback HTTP listener with SQLite, encrypted local secrets, shared filesystem storage, durable workflows, Worker, MediaProcessor, and Quartz. Only Internet acquisition (`IYtDlpClient`) and binary provisioning are replaced. The input video is generated with real FFmpeg; probing, hashing, upload, processing, playback, indexing, and search use production implementations. These tests do not verify third-party Internet downloads or browser rendering.

The tests cover:

- Concurrent HTTP storage writes; download submission, metadata, acquisition, upload, finalization, application-level duplicate delivery, progress fan-out, original/range playback, rendition SSE, real HLS processing and segment delivery, and real Typesense search.
- Incoming-file scan, staged scan-object ingestion, metadata edits, inclusion, real FFprobe, commit, hash/upload/finalization, scheduled database maintenance, persisted scheduling, and completed media surviving restart.
- Automatic Admin access, a new explicitly authorized endpoint without permission metadata, and a TCP canary that counts any attempted connections to removed infrastructure.
- Worker cancellation, a stopped run surviving restart, and explicit creation of a new run.
- Abrupt termination of a separate Lite process during acquisition, startup reconciliation, expired inbox redelivery, stale execution fencing, and explicit retry. The test advances the crashed inbox lease's expiry to avoid waiting its two-minute TTL.

`LiteCrashHost` supplies the integration executable's entry point. Normal invocations delegate to the generated Microsoft Testing Platform runner. The private `--lite-crash-host` invocation starts the deterministic host for the process-kill test and never runs the test framework.

Shared checks:

```sh
dotnet run --project Tests/UnitTests/UnitTests.csproj
# Set FROSTSTREAM_TEST_POSTGRES to an admin connection string for a local disposable test server.
# Provider tests create and drop isolated databases; the variable enables PostgreSQL alongside SQLite.
dotnet run --project Tests/UnitTests/UnitTests.csproj -- \
  --treenode-filter '/*/UnitTests.DataBridge/*/*'

# Full integration fixtures provision their own NATS/PostgreSQL/OpenFGA test containers.
dotnet run --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- \
  --treenode-filter '/*/IntegrationTests.Auth/*/*'
```

The Full suites also include namespaces `IntegrationTests.Storage`, `IntegrationTests.DataBridge`, `IntegrationTests.Cookies`, `IntegrationTests.WebApiHttp`, and `IntegrationTests.Maintenance`. Run them with the same filter shape. Full/Lite controller-route parity and unregistered-policy behavior are covered in the shared deployment and backend access tests.

Frontend access/navigation verification:

```sh
cd src/App/Frontend
pnpm test:access
pnpm check
```

Phase 3i validation: 572 shared unit tests, 116 DataBridge tests with both providers enabled, 3 Lite end-to-end tests, 85 Full integration tests, and 5 frontend access/navigation tests passed. Frontend type checking reported no errors or warnings. Backup implementation, deployment packaging, and release remain Phase 4 work.
