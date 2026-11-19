#!/usr/bin/env bash

set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"

# Reuse running containers even when this command comes from another worktree.
# Recreating them could switch the developer's bind-mounted database directory.
docker compose -f "$script_dir/local-dev/docker-compose.yml" \
  up -d --wait --no-recreate relisten-db relisten-redis

# Tests use disposable databases on the shared local server and SDK-managed
# Temporal servers. Every project runs even when an earlier one fails, so one
# failure can't hide another project's results; the script fails if any did.
status=0
RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test \
  "$script_dir/RelistenApiTests/RelistenApiTests.csproj" "$@" || status=1
dotnet test "$script_dir/RelistenUserServiceTests/RelistenUserServiceTests.csproj" "$@" || status=1
# Normalization's tests that need no database.
dotnet test "$script_dir/Relisten.NormalizationTests/Relisten.NormalizationTests.csproj" "$@" ||
  status=1
exit "$status"
