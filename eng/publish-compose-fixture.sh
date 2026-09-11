#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture_project="$repo_root/src/App/ComposePublishingFixture/ComposePublishingFixture.csproj"
output_root="${1:-$repo_root/artifacts/compose-publishing-fixture}"
prepared_root="${2:-$repo_root/artifacts/compose-publishing-fixture-prepared}"
comparison_root="${3:-}"
aspire_config="$repo_root/src/App/aspire.config.json"
aspire_config_backup="$(mktemp)"

cp "$aspire_config" "$aspire_config_backup"
restore_aspire_config() {
  cp "$aspire_config_backup" "$aspire_config"
}
trap restore_aspire_config EXIT

profiles=(
  frostream-full-init
  froststream-full
  frostream-lite-init
  frostream-lite
)

mkdir -p "$output_root"
mkdir -p "$prepared_root"

for profile in "${profiles[@]}"; do
  profile_output="$output_root/$profile"
  mkdir -p "$profile_output"
  dotnet tool run aspire publish \
    --apphost "$fixture_project" \
    --output-path "$profile_output" \
    --non-interactive \
    --nologo \
    -- \
    --deployment-profile "$profile"
done

for profile in "${profiles[@]}"; do
  profile_output="$prepared_root/$profile"
  mkdir -p "$profile_output"
  dotnet tool run aspire do "prepare-$profile" \
    --apphost "$fixture_project" \
    --output-path "$profile_output" \
    --environment phase2 \
    --non-interactive \
    --nologo \
    -- \
    --deployment-profile "$profile"
done

verifier_arguments=("$output_root" "$prepared_root")
if [[ -n "$comparison_root" ]]; then
  verifier_arguments+=("$comparison_root")
fi

dotnet run \
  --project "$repo_root/src/App/ComposePublishingFixture.Tests/ComposePublishingFixture.Tests.csproj" \
  -- "${verifier_arguments[@]}"

for profile in "${profiles[@]}"; do
  docker compose \
    --env-file "$output_root/$profile/.env" \
    -f "$output_root/$profile/docker-compose.yaml" \
    config --quiet
done
