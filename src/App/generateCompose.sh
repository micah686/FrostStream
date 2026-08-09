#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
apphost_project="$script_dir/AppHost/AppHost.csproj"
source_env="$script_dir/AppHost/aspire-development.env"
output_path="$script_dir/docker-compose-artifacts"

command -v aspire >/dev/null 2>&1 || { echo "Aspire CLI not found on PATH." >&2; exit 127; }
mkdir -p "$output_path"

publish_variant() {
  local dev="$1" compose_name="$2" env_name="$3" temp_env
  temp_env="$(mktemp)"
  grep -v '^FROSTSTREAM_DEV_TOOLS=' "$source_env" > "$temp_env"
  if [[ "$dev" == true ]]; then echo 'FROSTSTREAM_DEV_TOOLS="true"' >> "$temp_env"; else echo 'FROSTSTREAM_DEV_TOOLS="false"' >> "$temp_env"; fi
  local old_env_file="${FROSTSTREAM_ENV_FILE-}"
  local old_dev_tools="${FROSTSTREAM_DEV_TOOLS-}"
  export FROSTSTREAM_ENV_FILE="$temp_env"
  export FROSTSTREAM_DEV_TOOLS="$dev"
  (cd "$script_dir" && aspire publish --apphost "$apphost_project" -o "$output_path" --non-interactive --nologo)
  if [[ -n "$old_env_file" ]]; then export FROSTSTREAM_ENV_FILE="$old_env_file"; else unset FROSTSTREAM_ENV_FILE; fi
  if [[ -n "$old_dev_tools" ]]; then export FROSTSTREAM_DEV_TOOLS="$old_dev_tools"; else unset FROSTSTREAM_DEV_TOOLS; fi
  rm -f "$temp_env"

  local compose_file="$output_path/docker-compose.yaml"
  awk -v development="$dev" '
    /^services:$/ { in_services=1; print; next }
    in_services && /^[^ ]/ { in_services=0 }
    in_services && /^  [A-Za-z0-9][A-Za-z0-9_-]*:$/ {
      service=$0; sub(/^  /,"",service); sub(/:$/,"",service)
      if (development == "false" && service == "aspire-docker-demo-dashboard") { skip=1; next }
      print; print "    container_name: froststream-" service; next
    }
    skip && /^  [A-Za-z0-9][A-Za-z0-9_-]*:/ { skip=0 }
    skip { next }
    { print }
  ' "$compose_file" > "$compose_file.tmp"
  mv "$compose_file.tmp" "$output_path/$compose_name"
  cp "$output_path/.env" "$output_path/$env_name"
}

publish_variant true docker-compose-dev.yaml .env-dev
publish_variant false docker-compose.yaml .env
rm -f "$output_path/docker-compose.yaml.tmp" "$output_path/.env"
