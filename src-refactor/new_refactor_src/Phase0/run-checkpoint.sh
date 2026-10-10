#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
profile="${1:-unit}"
python3 Phase0/verify.py
case "$profile" in
  unit) project=Tests/UnitTests/UnitTests.csproj; filter='';;
  lite) project=Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj; filter='/*/IntegrationTests.Lite/*/*';;
  full) project=Tests/IntegrationTests/IntegrationTests/IntegrationTests.csproj; filter='/*/IntegrationTests.Auth/*/*';;
  *) echo 'Usage: run-checkpoint.sh unit|lite|full' >&2; exit 2;;
esac
mkdir -p .phase0/results
# Build separately to bound MSBuild parallelism on the laptop profile.
dotnet build "$project" -m:1 --nologo
test_args=(--results-directory "$root/.phase0/results/$profile")
if [[ -n "$filter" ]]; then test_args+=(--treenode-filter "$filter"); fi
dotnet run --no-build --project "$project" -- "${test_args[@]}"
