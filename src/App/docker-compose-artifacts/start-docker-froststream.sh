#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_file="$script_dir/docker-compose.yaml"
env_file="$script_dir/.env"

command -v docker >/dev/null 2>&1 || { echo "Docker CLI not found on PATH." >&2; exit 127; }
[[ -f "$compose_file" ]] || { echo "Production Compose file not found. Run src/App/generateCompose.sh first." >&2; exit 1; }
[[ -f "$env_file" ]] || { echo "Production environment file not found. Run src/App/generateCompose.sh first." >&2; exit 1; }

exec docker compose --env-file "$env_file" -f "$compose_file" up -d --build "$@"
