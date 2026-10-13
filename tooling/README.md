# Maintainer tooling

- `architecture/` contains source graph, provider boundary, publish and composition checks.
- `installation/` contains package generation, Compose validation, startup probes and test runners.
- `recovery/` contains fresh-install baseline and restore verification tools.
- `operations/` contains local runtime maintenance commands.
- `performance/` contains corpus generation and workload/resource measurement inputs.

Implementation reports and portable results are in `docs/implementation-history/` and `docs/verification/`. The search-corpus fixture used by unit tests lives in `Tests/UnitTests/TestData/`.
