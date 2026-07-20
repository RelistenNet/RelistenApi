#!/usr/bin/env bash

set -Eeuo pipefail

readonly DB_SERVICE="relisten-db"
readonly DB_USER="relisten"
readonly EXPECTED_TIMESCALEDB_VERSION="2.28.2"
readonly DATABASES=(relisten_db temporal temporal_visibility)

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
compose_file="$script_dir/docker-compose.yml"
compose=(docker compose -f "$compose_file")

die() {
  echo "error: $*" >&2
  exit 1
}

query() {
  local database="$1"
  local sql="$2"

  docker exec "$db_container" \
    psql -X -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$database" -Atc "$sql"
}

wait_for_postgres() {
  for _ in {1..60}; do
    if docker exec "$db_container" pg_isready -U "$DB_USER" -d postgres >/dev/null 2>&1; then
      return
    fi
    sleep 1
  done

  die "local PostgreSQL did not become ready within 60 seconds"
}

command -v docker >/dev/null 2>&1 || die "Docker is required"
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required"

"${compose[@]}" up -d "$DB_SERVICE"
db_container="$("${compose[@]}" ps -q "$DB_SERVICE")"
[[ -n "$db_container" ]] || die "could not find the $DB_SERVICE container"
wait_for_postgres

login_roles="$(query postgres \
  "select coalesce(string_agg(rolname, ',' order by rolname), '') from pg_roles where rolcanlogin and rolname !~ '^pg_';")"
[[ "$login_roles" == "$DB_USER" ]] || die \
  "expected relisten to be the only local login role; found: ${login_roles:-none}"

unexpected_roles="$(query postgres \
  "select coalesce(string_agg(rolname, ',' order by rolname), '') from pg_roles where rolname !~ '^pg_' and rolname <> '$DB_USER';")"
[[ -z "$unexpected_roles" ]] || die \
  "unexpected local roles found: $unexpected_roles. A prior restore may be incomplete; rerun ./restore-local-database.sh"

for database in "${DATABASES[@]}"; do
  owner="$(query postgres \
    "select pg_get_userbyid(datdba) from pg_database where datname = '$database';")"

  if [[ -z "$owner" ]]; then
    echo "Creating local database $database"
    docker exec "$db_container" \
      createdb -U "$DB_USER" --owner "$DB_USER" --template template0 "$database"
    owner="$DB_USER"
  fi

  [[ "$owner" == "$DB_USER" ]] || die \
    "database $database is owned by $owner, expected $DB_USER"
done

# A database restored under an older TimescaleDB library can let PostgreSQL
# start while failing later when application code first touches the extension.
# Surface that mismatch here, beside the command the developer already ran.
if ! installed_timescaledb_version="$(query relisten_db \
  "select extversion from pg_extension where extname = 'timescaledb';" 2>&1)"; then
  die "could not inspect the local TimescaleDB extension. Restore a current slim backup with ./restore-local-database.sh"
fi

if [[ -n "$installed_timescaledb_version" && \
      "$installed_timescaledb_version" != "$EXPECTED_TIMESCALEDB_VERSION" ]]; then
  die "relisten_db uses TimescaleDB $installed_timescaledb_version, but local development expects $EXPECTED_TIMESCALEDB_VERSION. Restore a current slim backup with ./restore-local-database.sh"
fi

echo "Local PostgreSQL is ready: ${DATABASES[*]} (owner: $DB_USER)"
