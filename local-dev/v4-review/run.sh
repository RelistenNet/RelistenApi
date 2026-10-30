#!/usr/bin/env bash
# Run the production API composition against the restored local catalog. Never seed or reset it.
set -Eeuo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
cd "$repo_dir"
./start-local-databases.sh
if [[ "$(PGPASSWORD=local_dev_password psql -X -At -h 127.0.0.1 -p 15432 -U relisten -d relisten_db -c "SELECT to_regclass('norm.track_songs') IS NOT NULL")" != t ]]; then
  echo 'Restore a real catalog with normalization data before starting C1 review.' >&2
  exit 1
fi
mkdir -p local-dev/v4-review/.output
dotnet build RelistenApi/RelistenApi.csproj --disable-build-servers -m:1
printf '%s\n' 'Catalog: restored local relisten_db (no reset or fixture inserts)' \
  'Swagger: http://127.0.0.1:43823/api-docs-v4' \
  'Capture responses in another terminal: ./local-dev/v4-review/capture.sh'
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://127.0.0.1:43823
export PORT=43823
export DATABASE_URL=postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db
export PGBOUNCER_WRITE_HOST=
export PGBOUNCER_READ_ONLY_HOST=
export REDIS_URL=redis://127.0.0.1:16379
export SENTRY_DSN=
# Client-only Temporal composition skips Hangfire recurring registration and starts no workers.
export Ingestion__Engine=Temporal
export ENABLE_HANGFIRE_SERVER=false
exec dotnet run --no-build --no-launch-profile --project RelistenApi/RelistenApi.csproj
