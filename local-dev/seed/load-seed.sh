#!/usr/bin/env bash
# Load the checked-in catalog only into an empty local database.
set -Eeuo pipefail

skip_existing=0
if [[ "${1:-}" == "--if-empty" ]]; then
  skip_existing=1
  shift
fi
if [[ $# -lt 1 || $# -gt 3 ]]; then
  echo "Usage: $0 [--if-empty] CONTAINER [DATABASE=relisten_db] [USER=relisten]" >&2
  exit 2
fi
container="$1"
database="${2:-relisten_db}"
user="${3:-relisten}"
seed_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
psql=(docker exec -i "$container" psql -X -v ON_ERROR_STOP=1 -U "$user" -d "$database")
existing_tables="$("${psql[@]}" -Atc "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema') AND schemaname NOT LIKE '_timescaledb%';")"
if [[ "$existing_tables" != 0 ]]; then
  if [[ "$skip_existing" == 1 ]]; then
    echo "Keeping existing local database $database."
    exit 0
  fi
  echo "Refusing to seed $database: it already contains tables." >&2
  exit 1
fi

# A single transaction leaves the database empty if schema or data loading fails.
# Decode before connecting so a damaged gzip cannot leave a partial seed behind.
seed_data="$(mktemp)"
trap 'rm -f "$seed_data"' EXIT
gzip -dc "$seed_dir/data.sql.gz" > "$seed_data"
{
  echo "BEGIN;"
  cat "$seed_dir/schema.sql"
  cat "$seed_data"
  echo "COMMIT;"
} | "${psql[@]}" > /dev/null
"${psql[@]}" -c ANALYZE > /dev/null
echo "Loaded the checked-in five-artist catalog into $database."
