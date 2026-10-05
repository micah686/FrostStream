# Installing Full and Lite

Both profiles come from the same AppHost and use the shared frontend and API.
Release CI tests API, repositories, durable workflows, access controls and SQLite
recovery, then builds and installs both Compose profiles on Linux amd64 and arm64.
A `v*` tag publishes the source installation archive, verified application image archives and SHA256SUMS only after
these checks succeed. The archive includes the generated Compose graphs, example
parameters and the deployment and recovery guides from that revision.

## Prerequisites and installation

Use Linux amd64 or arm64, Docker Engine with Compose v2, .NET 10 (see
`global.json`), Aspire CLI 13.4.6, and Python 3.12 or newer with PyYAML 6.0.2 for profile
validation. Image builds download their pinned media tools; runtime does not need
to download those tools. Allow sufficient disk space for Docker builds and media.

1. Download `froststream-release.tar.gz` and `SHA256SUMS` from the same release.
   Run `sha256sum -c SHA256SUMS`, then extract the archive.
2. In the extracted `froststream` directory, copy `src/App/profiles.env.example`
   to a private file outside the checkout. Replace its credential placeholders,
   including Typesense, PostgreSQL, OpenBao, Authentik and the restore console token.
   Restrict this file to your account (`chmod 600`).
3. Generate both profiles with:

   ```sh
   FROSTSTREAM_ENV_FILE=/absolute/path/to/private-profiles.env bash src/App/generateProfiles.sh
   ```

   The generator creates private `.env` files for Compose, and creates NATS TLS
   material locally. The release excludes private TLS keys, deployment credentials,
   OpenBao recovery material and database backups. Regeneration is required before
   starting the provided Compose graphs.
4. Choose `src/App/docker-compose-lite` or `src/App/docker-compose-full`. From
   that directory run `docker compose build` and `docker compose up -d`.
   Alternatively, download the matching `froststream-images-linux-amd64.tar.gz`
   or `froststream-images-linux-arm64.tar.gz`, verify it with the release checksum,
   run `docker load -i /path/to/froststream-images-linux-<architecture>.tar.gz`, then
   use `docker compose up -d --no-build --pull never`. Supporting service images
   still come from their pinned registries.
   Open `http://localhost:25000`. Full uses Authentik login; Lite provides the
   single local Admin identity and must be exposed only on a trusted network.

For production Full deployments, configure the HTTPS/public origin and identity
settings described in [running services](RUNNING_SERVICES.md). The supplied
example uses local HTTP for installation checks. `LIVE_CHAT_ENABLED=true` adds
optional ClickHouse to either profile. Typesense and the POT provider remain in
both profiles. Lite excludes NATS, PostgreSQL, OpenBao, Authentik, OpenFGA and the
PostgreSQL backup service.

## Persistent storage and backups

Lite persists its SQLite database, queues, workflows, staged objects, encrypted
secrets and complete Data Protection key ring in the Compose `/data` volume.
Defaults are `/data/frostreamlitedb`, `/data/backups/<timestamp>-<id>.sqlite` and
`/data/frostreamlitedb.keys`. Set `Persistence__Sqlite__Path`, `Backup__Directory`
and `Secrets__Local__KeyRingPath` in the private source environment before generating
profiles. Keep paths under `/data`, or add persistent mounts for every relocated
path. The protected backup page reports resolved paths and companion key backups.
Each recoverable snapshot includes `.keys/` and `.recovery.json` companions.

Full persists PostgreSQL, OpenBao and supporting services in their configured
volumes. `FROSTSTREAM_BACKUP_ROOT` controls the pgBackRest and paired OpenBao
backup bind directory. Preserve the separate `openbao-bootstrap/init.env` securely;
it contains vault recovery material. Follow [the backup guide](BACKUP_RESTORE.md)
for initialization, backups, verification and the Full restore console.
Media files and import sources are managed separately in both modes. Back up
optional ClickHouse independently. Typesense is rebuilt from application data.

## Recovery and upgrades

Before upgrading, create and verify a backup and preserve existing data, keys,
configuration and the previous release. Stop services with `docker compose down`,
extract the new release, regenerate profiles with your existing private configuration,
and rebuild/start images. Do not use `down --volumes` for an upgrade.

For Lite restore, stop the server before replacing files. Preserve the current
database, WAL/SHM files and entire key ring; replace the live database with the
chosen snapshot and restore all companion keys, including older keys. Restart
and check workflows, queues, staged objects and secrets. If validation fails,
retain the failed recovery set and roll back using the preserved original files.
The detailed shutdown, replacement and rollback procedure is in
[the recovery guide](BACKUP_RESTORE.md#lite-sqlite-snapshots-and-recovery).

## Release checks

`.github/workflows/release-verification.yml` runs shared checks and fresh archive
installations on disposable runners. `smoke_profiles.py` removes persistent volumes
and is restricted to `CI=true`; never run it against an existing installation.
Release archives contain source and Compose build instructions, plus application
images exported after installation checks. Choose a local build or load the image
archive for your architecture. CI artifacts are available for branch builds;
tagged releases are published only after both architecture checks pass. CI logs
and test output do not constitute a release until that workflow succeeds.
