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

# Another worktree may already own these containers and their data directories.
docker compose -f "$compose_file" up -d --no-recreate
"$script_dir/local-dev/ensure-databases.sh"
db_container="$(docker compose -f "$compose_file" ps -q relisten-db)"
"$script_dir/local-dev/seed/load-seed.sh" --if-empty "$db_container"

cat <<'EOF'

Local services are ready:
  PostgreSQL: 127.0.0.1:15432 (relisten / local_dev_password)
    database: relisten_db
  PgBouncer:  127.0.0.1:16432
  Redis:      127.0.0.1:16379
  Adminer:    http://localhost:18080

An empty catalog receives the checked-in development seed. Existing data is
preserved. See local-dev/README.md for tests and explicit restore commands.
EOF
