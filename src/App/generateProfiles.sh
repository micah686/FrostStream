#!/usr/bin/env bash
# Generate each profile under its application area.
set -euo pipefail
umask 077
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_env="${FROSTSTREAM_ENV_FILE:-$script_dir/SharedApp/AppHostCommon/aspire-development.env}"
[[ -f "$source_env" ]] || { echo "Missing environment file: $source_env" >&2; exit 1; }
command -v aspire >/dev/null || { echo 'Aspire CLI is required.' >&2; exit 127; }
temp_env="$(mktemp)"
trap 'rm -f "$temp_env"' EXIT
for mode in Full Lite; do
    # The explicit profile wins over both source-file and inherited mode selections.
    awk '!/^[[:space:]]*(Deployment__Mode|FROSTSTREAM_DEV_TOOLS)=/' "$source_env" > "$temp_env"
    printf '\nDeployment__Mode=%s\nFROSTSTREAM_DEV_TOOLS=false\n' "$mode" >> "$temp_env"
    output="$script_dir/${mode}App/docker-compose-${mode,,}"
    mkdir -p "$output"
    (cd "$script_dir/${mode}App" && \
        FROSTSTREAM_PROFILE_ENV_OUTPUT="$output/.env.resolved" FROSTSTREAM_COMPOSE_OUTPUT_KIND=profile FROSTSTREAM_ENV_FILE="$temp_env" Deployment__Mode="$mode" \
        aspire publish --apphost "$script_dir/${mode}App/AppHost${mode}/AppHost${mode}.csproj" -o "$output" --non-interactive --nologo)
    mv "$output/.env.resolved" "$output/.env"
    chmod 600 "$output/.env"
    echo "$mode: $output/docker-compose.yaml"
done
