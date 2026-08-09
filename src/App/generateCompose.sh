#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
apphost_project="$script_dir/AppHost/AppHost.csproj"
output_path="$script_dir/docker-compose-artifacts"

if ! command -v aspire >/dev/null 2>&1; then
  echo "Aspire CLI not found on PATH. Install the Aspire CLI before running generateCompose." >&2
  exit 127
fi

mkdir -p "$output_path"

cd "$script_dir"
aspire publish --apphost "$apphost_project" -o "$output_path" --non-interactive --nologo

# Compose normally names containers `<project>-<service>-1`. Pin stable names for this
# single-instance deployment so logs and scripts can refer to the service directly.
compose_file="$output_path/docker-compose.yaml"
awk '
  /^services:$/ { in_services=1; print; next }
  in_services && /^[^ ]/ { in_services=0 }
  in_services && /^  [A-Za-z0-9][A-Za-z0-9_-]*:$/ {
    service=$0
    sub(/^  /, "", service)
    sub(/:$/, "", service)
    print
    print "    container_name: froststream-" service
    next
  }
  { print }
' "$compose_file" > "$compose_file.tmp"
mv "$compose_file.tmp" "$compose_file"
