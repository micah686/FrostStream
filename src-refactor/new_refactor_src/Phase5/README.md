# Phase 5: embedded Lite search

Lite uses Lucene.Net 4.8.0-beta00018 inside the backend. Full retains its isolated Typesense adapter. The default generated Lite profile has exactly backend, frontend and POT provider; enabling live chat adds ClickHouse. Lite has no Typesense package, project, environment or container dependency. The frontend binds loopback unless `LITE_TRUSTED_LAN=true`; all reachable Lite clients receive local Admin access.

## Storage and recovery

`Search:Lucene:Path` identifies an exclusively owned, rebuildable directory. Published Lite mounts the separate `froststream-lite-search` volume at `/search`. Development uses a sibling search directory. Do not share it between backend processes. SQLite and media/caption sidecars remain authoritative; search volumes are excluded from application snapshots.

Active writes commit Lucene and refresh readers before returning, so pending revisions are acknowledged only after a durable commit. Rebuilds write a separate generation, retain the current reader, and replay revision checkpoints. Final activation holds a SQLite `BEGIN IMMEDIATE` barrier: WAL readers continue while source mutations wait. At most 128 changed media are replayed in that window, with a cooperative ten-second cancellation deadline and at most ten convergence attempts. Lucene commit/fsync itself is synchronous. Failed or canceled preparation retains the active generation and pending revisions. Publication uses a flushed manifest, atomic rename and Linux directory fsync. The current and previous generation directories are retained; older generations are pruned.

Startup checks index checksums and manifest schema. Missing, corrupt or incompatible indexes rebuild from authoritative data. SQLite snapshots mark their restored search state invalid, forcing reconstruction even if a newer search volume remains. The API exposes `/api/search/status`; library and search pages show building, degraded and failed status. A failed initial rebuild fails startup rather than serving a silently empty index.

Defaults: 16 MiB writer RAM buffer (`Search:Lucene:RamBufferMiB`, allowed 4–64), serial merges, imports of at most 500 documents, caption hydration one media at a time, and maximum result depth 100000 (`Search:Lucene:MaximumPageDepth`, allowed 250–100000). Requests exceeding that depth fail explicitly. Typed filters, token prefix matching, numeric ranges, private-note ownership, related documents and stable sort tie-breaking use the shared search contract. Existing endpoint paging semantics remain: metadata pages receive bounded caption/note augmentation; totals are not a globally ranked union. Tokenization/ranking is provider-specific, and Typesense typo tolerance is not implemented by Lucene.

## Verification (2026-10-11)

- Source graph inventory recaptured after reviewing the Lite Typesense removal, pinned Lucene packages, explicit Full adapter test reference and existing search-status route. Source containment/cycle/API inventory checks passed.
- Solution build passed (two existing Aspire CLI-bundle warnings).
- 617 unit tests and all 3 Lite workflow tests passed. Lite fixtures require no search container.
- Both real provider contract tests passed against Lucene and Full Typesense 30.2. Repeat with `FROSTSTREAM_TEST_TYPESENSE` set to an isolated Full search server.
- Lifecycle tests cover committed-write reopen/process kill, replacement readers, manifest publication failure, schema mismatch, corrupt/lost indexes, writer ownership, long transcripts and bounded paging. A separate real 1 MiB tmpfs run passed the disk-full recovery test.
- Lite workflow tests cover index loss/corruption and authoritative reconstruction after SQLite snapshot restore. SQLite barrier tests verify concurrent mutation exclusion and pending revision retention.
- Optional ClickHouse/trusted-LAN Compose generation passed (four services, no Typesense); optional integration runtime execution remains outside this validation.
- Three-container packaged frontend/API/auth smoke passed; embedded search reported Ready. See `packaged-smoke.json`.
- ARM64 framework-dependent publication passed; published dependencies contain Lucene and no Typesense. ARM64 runtime execution remains untested.
- Frontend `pnpm check` passed with zero errors/warnings. Boundary checks verify dependencies, loopback packaging and separate search storage.

`development-benchmark.json` records a reproducible synthetic 10000-media/100000-caption/250000-comment Lucene-only run: 18.48 seconds indexing, 3.08 seconds reopen, warm P95 2.80 ms, 40.0 MiB index and 226.8 MiB peak process RSS. This ran on a 32 GB x64 development host. It excludes frontend/POT/FFmpeg, cold cache and combined workloads. **The physical 8 GB acceptance gate, approved Phase 0 thresholds, idle measurement and 60-minute combined download/transcode/index run remain open. Phase 5's hardware exit gate is not claimed complete.**

From `new_refactor_src`:

```sh
dotnet build FrostStream.slnx -m:1
dotnet run --project Tests/UnitTests/UnitTests.csproj
dotnet run --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- --treenode-filter '/*/IntegrationTests.Lite/*/*'
# Generate Lite/docker-compose first (Phase0/publish-profile.sh Lite for a fresh owned fixture).
python3 Phase5/verify-boundaries.py
# Optional real Full adapter contract; use an isolated Typesense with API key phase4-test-key.
FROSTSTREAM_TEST_TYPESENSE=http://127.0.0.1:8108 dotnet run --project Tests/UnitTests/UnitTests.csproj -- --treenode-filter '/*/*/SearchProviderContractTests/*'
# Optional synthetic measurement; results are not physical-machine acceptance.
FROSTSTREAM_LUCENE_BENCHMARK_REPORT=/tmp/lucene-benchmark.json dotnet run --project Tests/UnitTests/UnitTests.csproj -- --treenode-filter '/*/*/LuceneBenchmarkTests/*'
# Optional destructive disk-full test: path must be a dedicated disposable bounded filesystem.
FROSTSTREAM_LUCENE_DISK_FULL_PATH=/dedicated-test-tmpfs dotnet run --project Tests/UnitTests/UnitTests.csproj -- --treenode-filter '/*/*/LuceneLifecycleTests/Disk_Full*'
```

The benchmark and disk-full tests return without exercising their optional workloads when their environment variables are absent; the normal unit count alone is not evidence for those workloads. Generated Compose and private fixture inputs remain ignored. No existing installations or volumes are migrated or deleted.
