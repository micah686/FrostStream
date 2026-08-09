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
