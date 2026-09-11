#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apphost="$repo_root/src/App/AppHost/AppHost.csproj"
profile="${1:-}"
output="${2:-}"
environment_file="${3:-}"
aspire_config="$repo_root/src/App/aspire.config.json"
aspire_config_backup="$(mktemp)"

if [[ "$profile" != "frostream-full-init" && "$profile" != "froststream-full" ]]; then
  echo "Phase 3 can publish frostream-full-init or froststream-full only." >&2
  exit 2
fi

if [[ -z "$output" ]]; then
  output="$repo_root/artifacts/production-compose/$profile"
fi
output="$(realpath -m "$output")"

cp "$aspire_config" "$aspire_config_backup"
restore_aspire_config() {
  cp "$aspire_config_backup" "$aspire_config"
}
trap restore_aspire_config EXIT

mkdir -p "$output"
app_arguments=(
  --deployment-profile "$profile"
  --deployment-output-path "$output"
)
if [[ -n "$environment_file" ]]; then
  app_arguments+=(--deployment-env "$(realpath "$environment_file")")
fi

dotnet tool run aspire publish \
  --apphost "$apphost" \
  --output-path "$output" \
  --non-interactive \
  --nologo \
  -- \
  "${app_arguments[@]}"

docker compose \
  --env-file "$output/.env" \
  -f "$output/docker-compose.yaml" \
  config --quiet
