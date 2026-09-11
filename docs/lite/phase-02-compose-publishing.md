# Phase 2: native four-profile Aspire publishing

Date verified: 2026-09-10

Status: Verified, awaiting acceptance.

This phase proves the required publisher behavior in an isolated fixture. It does not add the production Lite topology. The existing AppHost graph remains intact.

## Toolchain

- AppHost SDK: `Aspire.AppHost.Sdk` 13.5.3.
- Central Aspire hosting packages: 13.5.3.
- Repository-local Aspire CLI: 13.5.3, pinned by `.config/dotnet-tools.json` with roll-forward disabled.
- `AspireUseCliBundle` is enabled on both AppHosts so the CLI and AppHost use the compatible bundled publishing assemblies.
- SSH.NET remains centrally pinned to 2026.0.0 in `src/Directory.Packages.props`; restore and the complete solution build resolve successfully with that version.

Restore the pinned CLI with:

```bash
dotnet tool restore
dotnet tool run aspire --version
```

## Fixture structure

`DeploymentProfiles.Resolve` runs before `DistributedApplication.CreateBuilder`, so missing and unknown profiles fail before any Aspire resources are constructed. Its immutable selection contains a profile and the separate developer-tools flag. The profile contains the exact external name, edition, initialization flag, and installation identity.

| Profile | Edition | Init service | Compose name | Volume |
| --- | --- | --- | --- | --- |
| `frostream-full-init` | Full | Included | `froststream-full` | `froststream-full-fixture-data` |
| `froststream-full` | Full | Omitted | `froststream-full` | `froststream-full-fixture-data` |
| `frostream-lite-init` | Lite | Included | `froststream-lite` | `froststream-lite-fixture-data` |
| `frostream-lite` | Lite | Omitted | `froststream-lite` | `froststream-lite-fixture-data` |

Each invocation builds a new graph. The graph contains a persistent-volume database fixture, an application fixture with a parameter and database dependency, one edition marker, and an optional initialization job. Only init profiles add the job and the application's successful-completion dependency. Developer tooling appears only with `--developer-tools true` or `FROSTSTREAM_DEV_TOOLS=true`; publishing verification leaves it disabled.

The Compose environment uses Aspire's typed Docker Compose APIs:

- `AddDockerComposeEnvironment(profile.Name)`
- `WithDashboard(false)`
- `ConfigureComposeFile(file => file.Name = profile.InstallationName)`
- `ConfigureEnvFile(...)`
- `PublishAsDockerComposeService(...)`

No generated Compose or env file is changed after Aspire writes it.

## Reproduction

From the repository root:

```bash
dotnet tool restore
./eng/publish-compose-fixture.sh
```

The script publishes the four profiles beneath `artifacts/compose-publishing-fixture`, prepares resolved env files beneath `artifacts/compose-publishing-fixture-prepared`, runs the read-only verifier, and runs `docker compose config --quiet` for every published graph. Both artifact roots are ignored by Git. The script restores `src/App/aspire.config.json` after Aspire CLI updates its selected-AppHost setting.

The underlying native commands for one profile are:

```bash
dotnet tool run aspire publish \
  --apphost src/App/ComposePublishingFixture/ComposePublishingFixture.csproj \
  --output-path artifacts/compose-publishing-fixture/frostream-full-init \
  --non-interactive --nologo -- \
  --deployment-profile frostream-full-init

dotnet tool run aspire do prepare-frostream-full-init \
  --apphost src/App/ComposePublishingFixture/ComposePublishingFixture.csproj \
  --output-path artifacts/compose-publishing-fixture-prepared/frostream-full-init \
  --environment phase2 --non-interactive --nologo -- \
  --deployment-profile frostream-full-init
```

The fixture supplies deterministic synthetic parameter defaults based on edition, so prepare requires no prompt. Production secrets and configuration are outside this phase's scope.

## Publish and prepare contract

For all profiles, native publication creates `docker-compose.yaml` and `.env`. Compose retains `${FIXTURE_SHARED_KEY}`, while `.env` documents the parameter and leaves `FIXTURE_SHARED_KEY=` empty.

The `prepare-<profile>` step retains those publication files and additionally writes `.env.phase2` with the resolved synthetic value. Both profiles in each edition resolve the same value:

- Full: `full-resolved-fixture-value`
- Lite: `lite-resolved-fixture-value`

The verifier reads these files without modifying them and checks service membership, dependencies, dashboard absence, project identity, stable volumes, placeholders, empty publish values, and resolved prepare values.

## Verification evidence

The following completed successfully on 2026-09-10:

```text
dotnet build src/App/FrostStream.slnx --no-restore --verbosity minimal
Build succeeded. 0 Warning(s), 0 Error(s).

./eng/publish-compose-fixture.sh
8 Aspire pipelines succeeded (4 publish, 4 prepare).
Phase 2 Compose publishing fixture verification passed.
4/4 docker compose config validations passed.
```

Alternate-output and repeatability proof:

```bash
./eng/publish-compose-fixture.sh \
  /tmp/froststream-phase2-alternate \
  /tmp/froststream-phase2-alternate-prepared \
  artifacts/compose-publishing-fixture
```

This also passed all eight pipelines, graph checks, and four Compose validations. The verifier byte-compared every alternate `docker-compose.yaml` and `.env` with the first publication. All matched; no path, timestamp, or other native metadata differed.

## Manual review

Inspect one profile from each pair and confirm:

1. Both use the same top-level `name` and named volume.
2. Only the init graph contains `fixture-initialize` and `service_completed_successfully`.
3. Full and Lite contain only their matching edition marker.
4. No service name contains `dashboard` or `fixture-developer-tools`.
5. `.env` is unfilled and `.env.phase2` contains its edition's resolved synthetic value.

## Rollback

Remove the fixture, verifier, deployment-profile project, local tool manifest, and publishing script from the solution; remove the Phase 2 documentation; and restore the AppHost SDK line to its prior version. Generated files under the ignored artifact roots can be left in place or discarded. No production resource, volume, database, or runtime configuration is created by this proof.

## Limits

This validates native publisher semantics with synthetic Alpine resources. It does not prove that the future production Full or Lite graphs start successfully, nor does it replace the Phase 1 Full runtime limitation caused by the current Podman Compose lifecycle behavior.
