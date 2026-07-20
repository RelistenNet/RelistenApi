#!/usr/bin/env bash

set -Eeuo pipefail

readonly DB_SERVICE="relisten-db"
readonly DB_NAME="relisten_db"
readonly DB_USER="relisten"
readonly DB_PASSWORD="local_dev_password"
readonly EXPECTED_TIMESCALEDB_VERSION="2.28.2"
readonly RESTORE_EXPANSION_FACTOR=3
readonly MIN_FREE_AFTER_RESTORE_BYTES=$((5 * 1024 * 1024 * 1024))

dry_run=0
reset_local_cluster_requested=0
dump_arg=""
restore_started=0
db_container=""

usage() {
  cat <<'EOF'
Usage:
  ./restore-local-database.sh [OPTIONS] PATH_TO_DUMP

Explicitly wipes relisten_db and restores a PostgreSQL custom-format dump.
The empty temporal and temporal_visibility databases are preserved.

Options:
  --dry-run                     Validate the archive and print the restore plan.
  --reset-local-cluster         Reset all local PostgreSQL data if the existing
                                cluster cannot start with the pinned image. This
                                deletes temporal databases and their data.
  -h, --help                    Show this help text.
EOF
}

die() {
  echo "error: $*" >&2
  exit 1
}

log() {
  echo "==> $*"
}

run_sql() {
  local database="$1"
  local sql="$2"

  docker exec -i "$db_container" \
    psql -X -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$database" -c "$sql"
}

query_sql() {
  local database="$1"
  local sql="$2"

  docker exec "$db_container" \
    psql -X -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$database" -Atc "$sql"
}

wait_for_postgres() {
  for _ in {1..60}; do
    if docker exec "$db_container" pg_isready -U "$DB_USER" -d postgres >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done

  return 1
}

reset_local_cluster() {
  case "$pgdata_dir" in
    "$script_dir/local-dev/postgres-data/pgdata") ;;
    *) die "refusing to reset unexpected PGDATA path: $pgdata_dir" ;;
  esac

  log "Resetting incompatible local PostgreSQL data"
  "${compose[@]}" stop "$DB_SERVICE"
  "${compose[@]}" rm -f "$DB_SERVICE"
  rm -rf -- "$pgdata_dir"
  "${compose[@]}" up -d "$DB_SERVICE"
  db_container="$("${compose[@]}" ps -q "$DB_SERVICE")"
  [[ -n "$db_container" ]] || die "could not find the recreated $DB_SERVICE container"
  wait_for_postgres || die "recreated PostgreSQL did not become ready within 60 seconds"
}

check_disk_space() {
  local archive_bytes
  local available_bytes
  local current_database_bytes
  local effective_bytes
  local required_bytes

  archive_bytes="$(wc -c < "$dump_path" | tr -d ' ')"
  available_bytes="$(( $(df -Pk "$script_dir" | awk 'NR == 2 { print $4 }') * 1024 ))"
  current_database_bytes="$(query_sql postgres \
    "select coalesce(pg_database_size('$DB_NAME'), 0) where exists (select from pg_database where datname = '$DB_NAME');")"
  current_database_bytes="${current_database_bytes:-0}"

  # A custom archive has no reliable expansion ratio. Three archive sizes plus
  # a 5 GiB floor covers this known slim dump, restore WAL, and planner
  # statistics. Count only the database that this command is about to replace
  # as reclaimable; Temporal or unrelated Docker data is not ours to spend.
  effective_bytes=$((available_bytes + current_database_bytes))
  required_bytes=$((archive_bytes * RESTORE_EXPANSION_FACTOR + MIN_FREE_AFTER_RESTORE_BYTES))

  if (( effective_bytes < required_bytes )); then
    die "insufficient disk space for a guarded restore: need approximately $((required_bytes / 1024 / 1024 / 1024)) GiB including safety margin, but only $((effective_bytes / 1024 / 1024 / 1024)) GiB is available or reclaimable"
  fi

  log "Disk-space guard passed ($((effective_bytes / 1024 / 1024 / 1024)) GiB available or reclaimable)"
}

cleanup_restore_state() {
  if [[ "$restore_started" -eq 1 && -n "$db_container" ]]; then
    echo "Restore failed after timescaledb_pre_restore(); attempting timescaledb_post_restore() cleanup." >&2
    docker exec -i "$db_container" \
      psql -X -U "$DB_USER" -d "$DB_NAME" \
      -c "SELECT timescaledb_post_restore();" >/dev/null 2>&1 || true
  fi
}

normalize_timescale_policy_owners() {
  docker exec -i "$db_container" \
    psql -X -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" \
    < "$policy_normalization_file"
}

trap cleanup_restore_state ERR

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --dry-run)
      dry_run=1
      ;;
    --reset-local-cluster)
      reset_local_cluster_requested=1
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      [[ "$#" -eq 1 && -z "$dump_arg" ]] || die \
        "-- must be followed by exactly one dump path"
      dump_arg="$1"
      shift
      break
      ;;
    -*)
      die "unknown option: $1"
      ;;
    *)
      [[ -z "$dump_arg" ]] || die "only one dump path is supported"
      dump_arg="$1"
      ;;
  esac
  shift
done

[[ "$#" -eq 0 ]] || die "unexpected extra arguments"
[[ -n "$dump_arg" ]] || die "missing PATH_TO_DUMP"

case "$dump_arg" in
  /*) dump_path="$dump_arg" ;;
  *) dump_path="$(pwd -P)/${dump_arg#./}" ;;
esac
[[ -f "$dump_path" ]] || die "dump file does not exist: $dump_path"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
compose_file="$script_dir/local-dev/docker-compose.yml"
pgdata_dir="$script_dir/local-dev/postgres-data/pgdata"
policy_normalization_file="$script_dir/local-dev/normalize-timescale-policy-owners.sql"
compose=(docker compose -f "$compose_file")
[[ -f "$policy_normalization_file" ]] || die \
  "missing TimescaleDB policy mapping: $policy_normalization_file"

command -v docker >/dev/null 2>&1 || die "Docker is required"
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required"

log "Validating the archive with the pinned PostgreSQL client"
"${compose[@]}" run --rm --no-deps \
  --entrypoint pg_restore \
  --volume "$dump_path:/restore/app.dump:ro" \
  "$DB_SERVICE" --list /restore/app.dump >/dev/null

if [[ "$dry_run" -eq 1 ]]; then
  cat <<EOF
Would restore:
  archive:  $dump_path
  database: $DB_NAME
  owner:    $DB_USER

The restore would force-drop only $DB_NAME, preserve temporal databases, run
TimescaleDB pre/post hooks, normalize the three policy owners to $DB_USER, and
refresh planner statistics.
EOF
  exit 0
fi

log "Starting local PostgreSQL"
"${compose[@]}" up -d "$DB_SERVICE"
db_container="$("${compose[@]}" ps -q "$DB_SERVICE")"
[[ -n "$db_container" ]] || die "could not find the $DB_SERVICE container"

if ! wait_for_postgres; then
  if [[ "$reset_local_cluster_requested" -eq 1 ]]; then
    reset_local_cluster
  else
    die "PostgreSQL did not become ready. Inspect the container logs; if all local database data may be deleted, retry with --reset-local-cluster"
  fi
fi

db_image="$(docker inspect --format '{{.Config.Image}}' "$db_container")"

if ! available_timescaledb_version="$(query_sql postgres \
  "select default_version from pg_available_extensions where name = 'timescaledb';" 2>&1)"; then
  if [[ "$reset_local_cluster_requested" -eq 1 ]]; then
    reset_local_cluster
    available_timescaledb_version="$(query_sql postgres \
      "select default_version from pg_available_extensions where name = 'timescaledb';")"
  else
    die "could not inspect the pinned TimescaleDB library. Inspect the container logs; if all local database data may be deleted, retry with --reset-local-cluster"
  fi
fi
[[ "$available_timescaledb_version" == "$EXPECTED_TIMESCALEDB_VERSION" ]] || die \
  "local image provides TimescaleDB $available_timescaledb_version, expected $EXPECTED_TIMESCALEDB_VERSION"

check_disk_space

log "Stopping local database clients before the destructive restore"
"${compose[@]}" stop relisten-db-pgbouncer adminer

log "Dropping and recreating $DB_NAME"
# The archive's TimescaleDB policy rows refer to app by role OID. Keep that role
# non-login and privilege-free only long enough to load and rewrite those rows.
run_sql postgres "
  DO \$\$
  BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app') THEN
      CREATE ROLE app NOLOGIN;
    END IF;
  END
  \$\$;
  ALTER ROLE app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;"
run_sql postgres "DROP DATABASE IF EXISTS $DB_NAME WITH (FORCE);"
run_sql postgres "CREATE DATABASE $DB_NAME OWNER $DB_USER TEMPLATE template0;"

log "Preparing TimescaleDB restore state"
run_sql "$DB_NAME" "CREATE EXTENSION IF NOT EXISTS timescaledb;"
run_sql "$DB_NAME" "SELECT timescaledb_pre_restore();"
restore_started=1

log "Restoring the slim archive in dependency order"
docker run --rm \
  --network "container:$db_container" \
  --entrypoint pg_restore \
  --env "PGPASSWORD=$DB_PASSWORD" \
  --mount "type=bind,src=$dump_path,dst=/restore/app.dump,readonly" \
  "$db_image" \
  --exit-on-error \
  --no-owner \
  --no-privileges \
  --host=127.0.0.1 \
  --port=5432 \
  --username="$DB_USER" \
  --dbname="$DB_NAME" \
  /restore/app.dump

log "Returning TimescaleDB to normal operation"
run_sql "$DB_NAME" "SELECT timescaledb_post_restore();"
restore_started=0

log "Normalizing restored TimescaleDB policy ownership"
normalize_timescale_policy_owners

log "Refreshing planner statistics"
run_sql "$DB_NAME" "ANALYZE;"

"$script_dir/local-dev/ensure-databases.sh"

extensions="$(query_sql "$DB_NAME" \
  "select string_agg(extname || '=' || extversion, ',' order by extname) from pg_extension where extname in ('pg_stat_statements', 'pgcrypto', 'tablefunc', 'timescaledb');")"
[[ "$extensions" == *"timescaledb=$EXPECTED_TIMESCALEDB_VERSION"* ]] || die \
  "restored database does not use TimescaleDB $EXPECTED_TIMESCALEDB_VERSION"

log "Local database restore complete"
cat <<EOF
  database:   $DB_NAME
  owner:      $DB_USER
  extensions: $extensions

Run ./start-local-databases.sh to start Redis, PgBouncer, and Adminer.
EOF
