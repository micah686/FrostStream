#!/usr/bin/env bash
# Generate sibling directories so the existing portable AppHost config/build paths work.
set -euo pipefail
umask 077
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_env="${FROSTSTREAM_ENV_FILE:-$script_dir/AppHost/aspire-development.env}"
[[ -f "$source_env" ]] || { echo "Missing environment file: $source_env" >&2; exit 1; }
command -v aspire >/dev/null || { echo 'Aspire CLI is required.' >&2; exit 127; }
temp_env="$(mktemp)"
trap 'rm -f "$temp_env"' EXIT
for mode in Full Lite; do
    # The explicit profile wins over both source-file and inherited mode selections.
    awk '!/^[[:space:]]*(Deployment__Mode|FROSTSTREAM_DEV_TOOLS)=/' "$source_env" > "$temp_env"
    printf '\nDeployment__Mode=%s\nFROSTSTREAM_DEV_TOOLS=false\n' "$mode" >> "$temp_env"
    output="$script_dir/docker-compose-${mode,,}"
    mkdir -p "$output"
    FROSTSTREAM_PROFILE_ENV_OUTPUT="$output/.env.resolved" FROSTSTREAM_ENV_FILE="$temp_env" Deployment__Mode="$mode" \
        aspire publish --apphost "$script_dir/AppHost/AppHost.csproj" -o "$output" --non-interactive --nologo
    mv "$output/.env.resolved" "$output/.env"
    chmod 600 "$output/.env"
    echo "$mode: $output/docker-compose.yaml"
done
