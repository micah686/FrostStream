# Phase 4 — Backups, packaging, and release

This breaks Phase 4 of [SIDE_PLAN.MD](SIDE_PLAN.MD) into ordered, reviewable subphases. It assumes Phase 3's Lite host, SQLite persistence, encrypted local secrets, shared backup routes, and capability reporting are in place. Keep backup behavior behind `IBackupServiceClient`, preserve Full's existing HTTP-backed behavior, and keep the shared application and frontend as the single feature implementation.

## Phase 4a — SQLite backup service

- Implement the Lite `IBackupServiceClient` with SQLite's online backup API, writing into the configured backup directory.
- Support full snapshots and verification. Publish a snapshot only after creation and verification both succeed; clean up incomplete artifacts on failure.
- Cover application data, workflow and queue state, staged objects, and encrypted secrets. Keep media files and optional ClickHouse data outside this backup scope.
- Bind Full to its existing HTTP backup client and Lite to the embedded implementation at startup.
- Return consistent errors for unsupported differential and point-in-time recovery operations.

**Completion:** Lite can create and verify a complete SQLite snapshot while the application is active. Failed or cancelled operations do not expose a partial backup, unsupported operations fail consistently, and Full retains its existing backup behavior.

## Phase 4b — Shared backup experience and capabilities

- Connect the existing backup endpoints, UI, and scheduling flows to the selected `IBackupServiceClient`.
- Show the resolved live database location and backup directory in the shared backup page.
- Advertise Lite's full-snapshot and verification support, and hide differential/PITR controls in Lite while retaining Full's supported options.
- Keep routes shared and make unsupported requests return the same clear result whether initiated from the UI or API.

**Completion:** Backup operations and status are usable through the shared API and UI in both modes; Lite presents only operations it supports and displays the actual resolved storage paths.

**Implementation:** Lite capabilities now advertise full SQLite snapshots and integrity verification, with differential, incremental, deep verification, and PITR unavailable. Shared backup handlers validate operations before dispatch; unsupported requests return a clear 400 response, missing snapshots return 404, and integrity failures return 422. Repository responses carry the resolved Lite database and backup directory (`Backup:Directory`, with `Persistence:Sqlite:BackupPath` as a fallback, defaults to `/data/backups`); paths remain within the protected backup API. The shared page displays these paths, offers latest/per-snapshot verification, refreshes the repository immediately after completed operations, and hides unsupported controls and PostgreSQL recovery metadata in Lite. Lite's job list explicitly describes its server-session lifetime. Shared schedule validation rejects enabled differential backup schedules when unsupported, and Quartz skips existing unsupported definitions during hydration and changes. Full retains its HTTP adapter, differential backups, deep verification, and PITR controls. Companion key backups and restore instructions remain in 4c.

**Validation:** Assembled Lite-host tests exercise capability reporting, resolved paths, HTTP snapshot creation and verification, job status/listing, unsupported-operation errors, missing snapshots, rejected differential schedules, skipped seeded differential jobs, and scheduled full snapshots. Full handler tests cover differential/deep dispatch and rejection before dispatch. Frontend type checking and access/navigation tests verify the shared frontend.

## Phase 4c — Recovery and encryption-key preservation

- Document and validate the supported restore procedure: stop the server, preserve the current database, replace it with the chosen snapshot named `frostreamlitedb`, then restart.
- Explain that WAL/SHM files may be handled only after shutdown and that media and optional ClickHouse data are managed separately.
- Preserve the complete local Data Protection key ring when making a recovery copy, including older keys required to decrypt secrets after key rotation.
- Include a companion key backup with a recoverable database backup so restoring onto another installation retains access to encrypted secrets.
- Verify restored workflow state, durable queues, staged objects, and secrets; retain and clearly report a recoverable copy if restore validation fails.

**Completion:** The documented shutdown/file-replacement workflow restores a usable Lite installation, including encrypted secrets, and makes the database/key-ring recovery requirements explicit.

**Implementation:** New Lite snapshots publish a standalone SQLite database together with `<label>.keys/` (the complete local secret key ring) and `<label>.recovery.json` (versioned database/key-file checksums). Keys are copied after the online snapshot; verification checks hashes, database integrity, foreign keys, and decryption of every saved secret with automatic key generation disabled. The database is published last, and failures clean up staged companions while leaving live data and keys untouched. Companion directories/files use owner-only Unix permissions. Protected repository responses expose the resolved live key path and companion presence; older database-only snapshots are flagged and cannot pass complete recovery verification. The shared Lite backup page now gives offline restore and rollback instructions. [The backup/recovery guide](../docs/BACKUP_RESTORE.md) documents shutdown, preserving the original database/key ring and WAL/SHM files, copying the selected database to `frostreamlitedb` and all companion keys to the resolved key location, post-restart checks, and rollback without overwriting the recoverable copy. Media/import sources and ClickHouse remain separately managed; Typesense is rebuilt. Full's recovery console and adapter remain unchanged.

**Validation:** Tests exercise key rotation, complete companion preservation, default/custom key locations, restoration by shutdown/file replacement, a second installation, restored workflow state, queue delivery, staged import objects, and secrets. Failed decryption leaves the saved database/key ring intact and rollback restores the previous credentials. Missing/changed companion files and absent keys prevent verification/publication; staged artifacts are cleaned up. Assembled-host HTTP checks cover companion metadata and 422 recovery errors while preserving live credentials.

## Phase 4d — Reproducible Full and Lite deployment profiles

- Generate both deployment profiles from the same AppHost configuration, selecting services and adapters by deployment mode.
- Package the Lite C# host, shared frontend, and required media tools.
- Exclude NATS, PostgreSQL, OpenBao, Authentik, OpenFGA, and the PostgreSQL backup service from the Lite profile; retain supporting services required by the plan, including Typesense and configured optional integrations.
- Make persistent database and backup locations configurable while preserving the defaults `/data/frostreamlitedb` and `/data/backups/<timestamp>-<id>.sqlite`.
- Document installation prerequisites, persistent storage, configuration, backup location, and restore steps for each profile.

**Completion:** A clean build produces installable Full and Lite profiles from the shared AppHost configuration, with Lite carrying its required runtime assets and no removed infrastructure dependencies.

**Implementation:** Separate Full and Lite AppHosts publish their own installable Compose profiles with private, populated parameter files and development tools disabled. The app-local `generate-compose.sh` scripts generate each profile. The Lite Dockerfile packages ffmpeg/ffprobe, versioned yt-dlp and Deno, and the matching bgutil plugin at build time. Full service Dockerfiles now include the extracted ApplicationContracts dependency. Lite database, backup and key-ring locations are configurable and persist under the default `/data` volume. [Deployment instructions](../docs/DEPLOYMENT_PROFILES.md) cover prerequisites, configuration, image builds, storage overrides, backups, upgrades and each profile's recovery procedure; generated local artifacts are ignored by Git.

**Validation:** AppHost builds and publishes both profiles. Generated-profile checks pass with ClickHouse disabled and enabled; Compose interpolation preserves custom database, backup and key-ring paths. All seven Full application/PostgreSQL images, the Lite image, and the shared frontend image build successfully on Linux amd64. Lite's packaged tools execute with networking disabled, schema-only initialization succeeds, and an isolated Lite container with Typesense/POT reaches `/health` and returns the expected SQLite capabilities. Full installation/runtime release checks, arm64 execution and CI publication remain in 4e.


## Phase 4e — Release verification

- Run shared API, repository, and workflow checks against both deployment profiles and assert route parity.
- Test backup creation during activity, snapshot verification failure, cancellation/cleanup, and the documented shutdown/file-replacement restore procedure, including key-ring recovery.
- Verify Lite's resolved paths, capability/UI behavior, unsupported-operation handling, and zero connection attempts to removed services.
- Verify Full's existing backup client, authentication, and deployment behavior remain intact.
- Run clean installation/package checks in CI and publish the release artifacts with the matching deployment and recovery documentation.

**Completion:** Both profiles install reproducibly and pass CI. Backup and restore behavior is validated for Lite, Full behavior remains intact, and release artifacts and operating instructions are ready for use.


**Implementation:** Release verification now runs the shared unit/integration suites and frontend access, backup-page rendering, type and build checks in GitHub Actions. HTTP method/route parity and Full's HTTP backup adapter contract are asserted explicitly; Lite backup checks cover concurrent commits and cancellation after staging. The existing Full authentication test now waits for provisioning to finish before asserting bootstrap tuples. Native amd64/arm64 jobs generate both AppHost profiles with and without optional ClickHouse, package the matching source/Compose graphs and deployment/recovery guides without credentials, extract that archive into a clean installation, build/start both profiles, and check capabilities, authentication boundaries, Lite paths/unsupported requests, online backup/verification failure/offline database-and-key-ring replacement, Full pgBackRest/OpenBao pairing and verification, and Lite tools with networking disabled. Verified application images are exported with architecture checks and SHA256 checksums. Version tags publish artifacts only after the complete verification matrix succeeds. [Deployment instructions](../docs/DEPLOYMENT_PROFILES.md) cover building or loading release images, private configuration, persistence, upgrades and recovery.

**Validation:** Local checks passed all 581 unit tests, all 88 integration tests (including both Full infrastructure and Lite runtime checks), frontend type checking, access/navigation tests and rendered Full/Lite backup-page tests. Both default AppHost profiles generated and passed structural checks; packaging verification confirmed matching guides, source/build assets, sanitized example parameters and the checksum. Native clean Compose installations, image export and GitHub release publication are implemented in CI and require a successful workflow run before this subphase's release completion can be claimed.
