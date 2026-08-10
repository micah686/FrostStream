#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
apphost_project="$script_dir/AppHost/AppHost.csproj"
source_env="$script_dir/AppHost/aspire-development.env"
# Defaults to the committed artifacts; override to generate elsewhere (mirrors -OutputPath in
# the PowerShell script), which is handy for inspecting a change before overwriting them.
output_path="${1:-$script_dir/docker-compose-artifacts}"

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
  rm -f "$output_path/.env"
  (cd "$script_dir" && aspire publish --apphost "$apphost_project" -o "$output_path" --non-interactive --nologo)
  if [[ -n "$old_env_file" ]]; then export FROSTSTREAM_ENV_FILE="$old_env_file"; else unset FROSTSTREAM_ENV_FILE; fi
  if [[ -n "$old_dev_tools" ]]; then export FROSTSTREAM_DEV_TOOLS="$old_dev_tools"; else unset FROSTSTREAM_DEV_TOOLS; fi
  rm -f "$temp_env"

  local compose_file="$output_path/docker-compose.yaml"
  awk -v development="$dev" '
    /^services:$/ { in_services=1; print; next }
    # Leaving the services block (networks:, volumes:) also ends any active skip.
    in_services && /^[^ ]/ { in_services=0; skip=0 }
    in_services && /^  [A-Za-z0-9][A-Za-z0-9_-]*:$/ {
      service=$0; sub(/^  /,"",service); sub(/:$/,"",service)
      # Every service header ends the previous service, and therefore any skip it started.
      # Deciding this here (rather than in a later rule) is required: this rule consumes the
      # line with next, so a rule below it would never see a service header at all.
      skip = (development == "false" && service == "aspire-docker-demo-dashboard")
      if (skip) next
      print; print "    container_name: froststream-" service; next
    }
    skip { next }
    { print }
  ' "$compose_file" > "$compose_file.tmp"
  mv "$compose_file.tmp" "$output_path/$compose_name"
  # The non-development variant writes .env in place; copying it onto itself fails under set -e.
  if [[ "$output_path/.env" != "$output_path/$env_name" ]]; then
    cp "$output_path/.env" "$output_path/$env_name"
  fi
}

publish_variant true docker-compose-dev.yaml .env-dev
publish_variant false docker-compose.yaml .env

awk '
  FILENAME == ARGV[1] {
    if ($0 ~ /^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=/) {
      key=$0; sub(/^[[:space:]]*/, "", key); sub(/=.*/, "", key)
      value=$0; sub(/^[^=]*=/, "", value)
      source_values[key]=value
    }
    next
  }
  {
    if ($0 ~ /^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=/) {
      key=$0; sub(/^[[:space:]]*/, "", key); sub(/=.*/, "", key)
      if (key in source_values) { print key "=" source_values[key]; next }
    }
    print
  }
' "$source_env" "$output_path/.env" > "$output_path/example.env"

rm -f "$output_path/docker-compose.yaml.tmp"
