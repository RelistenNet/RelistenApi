#!/usr/bin/env bash

set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
compose_file="$script_dir/local-dev/docker-compose.yml"

command -v docker >/dev/null 2>&1 || {
  echo "error: Docker is required" >&2
  exit 1
}
docker compose version >/dev/null 2>&1 || {
  echo "error: Docker Compose v2 is required" >&2
  exit 1
}

docker compose -f "$compose_file" up -d
"$script_dir/local-dev/ensure-databases.sh"

cat <<'EOF'

Local services are ready:
  PostgreSQL: 127.0.0.1:15432 (relisten / local_dev_password)
    databases: relisten_db, temporal, temporal_visibility
  PgBouncer:  127.0.0.1:16432
  Redis:      127.0.0.1:16379
  Adminer:    http://localhost:18080

This command never downloads or replaces database contents. See
local-dev/README.md for the explicit slim-backup restore command.
EOF
