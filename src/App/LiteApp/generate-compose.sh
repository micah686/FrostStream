#!/usr/bin/env bash
set -euo pipefail
umask 077

app_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_app_dir="$(cd "$app_dir/.." && pwd)"
output="$(realpath -m "${1:-$app_dir/docker-compose}")"
source_env="${FROSTSTREAM_ENV_FILE:-$repo_app_dir/SharedApp/AppHostCommon/aspire-development.env}"
apphost="$app_dir/AppHostLite/AppHostLite.csproj"

[[ -f "$source_env" ]] || { echo "Missing environment file: $source_env" >&2; exit 1; }
command -v aspire >/dev/null || { echo 'Aspire CLI is required.' >&2; exit 127; }
[[ "$output" == "$app_dir"/* ]] || { echo "Output must be inside $app_dir (Compose bind paths are app-relative)." >&2; exit 1; }
mkdir -p "$output"

temp_env="$(mktemp)"
trap 'rm -f "$temp_env"' EXIT
awk '!/^[[:space:]]*(Deployment__Mode|FROSTSTREAM_DEV_TOOLS)=/' "$source_env" > "$temp_env"
printf '\nDeployment__Mode=Lite\nFROSTSTREAM_DEV_TOOLS=false\n' >> "$temp_env"

(cd "$app_dir" && \
  FROSTSTREAM_PROFILE_ENV_OUTPUT="$output/.env.resolved" \
  FROSTSTREAM_COMPOSE_OUTPUT_KIND=profile \
  FROSTSTREAM_ENV_FILE="$temp_env" \
  Deployment__Mode=Lite \
  aspire publish --apphost "$apphost" -o "$output" --non-interactive --nologo)

mv "$output/.env.resolved" "$output/.env"
chmod 600 "$output/.env"
echo "Lite Compose and environment generated in $output"
