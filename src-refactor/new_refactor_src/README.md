# FrostStream refactor working tree

This is the self-contained Phase 0 working baseline. Run all commands here. No project or linked source depends on the old repository `src/` or the reference copy. `Phase0/source-map.json` records the mechanical relocation. Assembly names, namespaces, feature implementations and provider choices are preserved.

The requested Shared/Lite/Full/Frontend structure is present. Reserved directories have README files explaining their future extraction. Shared/Modules still contain the original executables; Full/Hosts shells and provider isolation are Phase 1/2 work. A directory name does not imply the target dependency rules already hold.

Prerequisites: .NET 10, Node >=22, pnpm 10.28.1, Python 3, FFmpeg/FFprobe, a Docker-compatible engine, and Aspire CLI for profile generation. `Phase0/check-compose.py` additionally requires the pinned `Phase0/requirements.txt` dependency. Rootless Podman fixtures use `DOCKER_HOST=unix:///run/user/$(id -u)/podman/podman.sock` and `TESTCONTAINERS_RYUK_DISABLED=true`.

```sh
dotnet build FrostStream.slnx -m:1
python3 Phase0/verify.py
python3 Phase0/test_verify.py
bash Phase0/run-checkpoint.sh unit
bash Phase0/run-checkpoint.sh lite
bash Phase0/run-checkpoint.sh full
cd Frontend
pnpm install --frozen-lockfile
pnpm test:access
pnpm test:backups
pnpm check
pnpm build
```

The `full` checkpoint runs real OpenFGA with controlled JWT identities. It does not certify Authentik login or a complete Full deployment. Lite fixtures exercise real local persistence, processing, HTTP playback and Typesense, with deterministic Internet acquisition. Each fixture owns fresh temporary state and containers; retries do not reuse an installation. Do not run the old repository's destructive smoke/reset scripts against developer state.

```sh
# From this directory; generates private fresh-install inputs, then Compose without starting it.
python3 Phase0/prepare-environment.py Lite
python3 Phase0/prepare-environment.py Full
bash Phase0/publish-profile.sh Lite
bash Phase0/publish-profile.sh Full
python3 Phase0/check-compose.py Lite Full
```

Generated profiles are under `Lite/docker-compose/` and `Full/docker-compose/`. These commands refuse to overwrite an existing fixture. If publishing fails, inspect and retry the logged Aspire command against that owned output. No automated cleanup deletes volumes. Full's baseline volumes have explicit names: packaged install/restore validation requires a dedicated disposable container engine. Generation/path validation is distinct from booting a clean packaged installation. Lite currently generates four services including Typesense; the three-service Lucene target starts in Phase 5. The baseline frontend binds all interfaces; use a private test machine until the planned loopback packaging change.

```sh
python3 Phase0/generate-corpus.py .phase0/corpus
python3 Phase0/measure.py --output .phase0/results/lite-resources.json --label lite-development --seconds 600 -- \
  dotnet run --no-build --project Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj -- \
  --treenode-filter '/*/IntegrationTests.Lite/*/*'
```

Generated corpus JSONL is input for future provider seeders, not evidence that the large corpus has been indexed. Search parser characterization runs from `Phase0/search-corpus.json`. Read `Phase0/CHECKLIST.md`, `Phase0/EVALUATION.md`, and the reconciled `TESTS_PLAN.MD` before declaring Phase 0's acceptance gates complete.
