#!/usr/bin/env bash

set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"

# Reuse running containers even when this command comes from another worktree.
# Recreating them could switch the developer's bind-mounted database directory.
docker compose -f "$script_dir/local-dev/docker-compose.yml" \
  up -d --wait --no-recreate relisten-db relisten-redis

# The suite creates and removes its own database on the shared local server.
# Temporal tests start temporary SDK-managed servers; no worker or schedule is started.
RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test \
  "$script_dir/RelistenApiTests/RelistenApiTests.csproj" "$@"
