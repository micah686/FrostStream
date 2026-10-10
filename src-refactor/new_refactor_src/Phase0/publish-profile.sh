#!/usr/bin/env bash
set -euo pipefail
umask 077
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
edition="${1:-}"
case "$edition" in Lite) project=AppHostLite;; Full) project=AppHostFull;; *) echo 'Usage: publish-profile.sh Lite|Full' >&2; exit 2;; esac
source_env="$root/.phase0/installations/$edition/fixture.env"
[[ -f "$source_env" ]] || { echo 'Run Phase0/prepare-environment.py for this edition first.' >&2; exit 1; }
output="$root/$edition/docker-compose"
[[ ! -e "$output" ]] || { echo 'Refusing to overwrite an existing Compose fixture.' >&2; exit 1; }
command -v aspire >/dev/null
mkdir -p "$output"
cd "$root/$edition"
FROSTSTREAM_ENV_FILE="$source_env" \
FROSTSTREAM_PROFILE_ENV_OUTPUT="$output/.env.resolved" \
FROSTSTREAM_COMPOSE_OUTPUT_KIND=profile \
aspire publish --apphost "$root/$edition/AppHost/$project.csproj" -o "$output" --non-interactive --nologo
mv "$output/.env.resolved" "$output/.env"
chmod 600 "$output/.env"
echo "Published $edition at $output; not started."
