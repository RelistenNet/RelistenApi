#!/usr/bin/env bash

set -Eeuo pipefail

DB_SERVICE="relisten-db"
DB_NAME="relisten_db"
DB_USER="relisten"
EXPECTED_TIMESCALEDB_VERSION="${EXPECTED_TIMESCALEDB_VERSION:-2.19.3}"

dry_run=0
dump_arg=""
restore_started=0
db_container=""

usage() {
  cat <<'EOF'
Usage:
  ./restore-local-database.sh [--dry-run] PATH_TO_DUMP

Wipes the local Docker Postgres database and restores it from a PostgreSQL
custom-format dump file.

Local target:
  service:  relisten-db
  database: relisten_db
  user:     relisten

Options:
  --dry-run  Print the commands that would run without changing the database.
  -h, --help Show this help text.
EOF
}

die() {
  echo "error: $*" >&2
  exit 1
}

log() {
  echo "==> $*"
}

quote_cmd() {
  printf "%q " "$@"
  printf "\n"
}

run() {
  echo "+ $(quote_cmd "$@")"
  if [ "$dry_run" -eq 0 ]; then
    "$@"
  fi
}

run_sql() {
  local database="$1"
  local sql="$2"

  run docker exec -i "$db_container" \
    psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$database" -c "$sql"
}

query_sql() {
  local database="$1"
  local sql="$2"

  docker exec -i "$db_container" \
    psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$database" -Atc "$sql"
}

wait_for_postgres() {
  log "Waiting for local Postgres to accept connections"
  if [ "$dry_run" -eq 1 ]; then
    echo "+ docker exec $db_container pg_isready -U $DB_USER -d postgres"
    return
  fi

  for _ in {1..60}; do
    if docker exec "$db_container" pg_isready -U "$DB_USER" -d postgres >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
  docker exec "$db_container" pg_isready -U "$DB_USER" -d postgres >/dev/null
}

cleanup_restore_state() {
  if [ "$restore_started" -eq 1 ] && [ -n "$db_container" ]; then
    echo "Restore failed after timescaledb_pre_restore(); attempting timescaledb_post_restore() cleanup." >&2
    docker exec -i "$db_container" \
      psql -U "$DB_USER" -d "$DB_NAME" -c "SELECT timescaledb_post_restore();" >/dev/null 2>&1 || true
  fi
}

trap cleanup_restore_state ERR

while [ "$#" -gt 0 ]; do
  case "$1" in
    --dry-run)
      dry_run=1
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      break
      ;;
    -*)
      die "unknown option: $1"
      ;;
    *)
      if [ -n "$dump_arg" ]; then
        die "only one dump path is supported"
      fi
      dump_arg="$1"
      ;;
  esac
  shift
done

if [ "$#" -gt 0 ]; then
  if [ -n "$dump_arg" ]; then
    die "only one dump path is supported"
  fi
  dump_arg="$1"
  shift
fi

[ "$#" -eq 0 ] || die "unexpected extra arguments"
[ -n "$dump_arg" ] || die "missing PATH_TO_DUMP"

case "$dump_arg" in
  /*) dump_path="$dump_arg" ;;
  *) dump_path="$(pwd -P)/${dump_arg#./}" ;;
esac

[ -f "$dump_path" ] || die "dump file does not exist: $dump_path"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
compose_file="$script_dir/local-dev/docker-compose.yml"
pgdata_dir="$script_dir/local-dev/postgres-data/pgdata"
[ -f "$compose_file" ] || die "missing compose file: $compose_file"

if docker compose version >/dev/null 2>&1; then
  compose=(docker compose -f "$compose_file")
elif command -v docker-compose >/dev/null 2>&1; then
  compose=(docker-compose -f "$compose_file")
else
  die "docker compose or docker-compose is required"
fi

if command -v pg_restore >/dev/null 2>&1; then
  log "Validating dump archive"
  echo "+ pg_restore --list $(printf "%q" "$dump_path") >/dev/null"
  if [ "$dry_run" -eq 0 ]; then
    pg_restore --list "$dump_path" >/dev/null
  fi
else
  log "Host pg_restore not found; skipping dump preflight validation"
fi

log "Starting local Postgres"
run "${compose[@]}" up -d "$DB_SERVICE"

if [ "$dry_run" -eq 1 ]; then
  db_container="<${DB_SERVICE}-container>"
else
  db_container="$("${compose[@]}" ps -q "$DB_SERVICE")"
  [ -n "$db_container" ] || die "could not find a running $DB_SERVICE container"
fi

wait_for_postgres

log "Checking local TimescaleDB extension version"
if [ "$dry_run" -eq 1 ]; then
  echo "+ docker exec $db_container psql -U $DB_USER -d postgres -Atc \"select default_version from pg_available_extensions where name = 'timescaledb'\""
else
  version_query="select default_version from pg_available_extensions where name = 'timescaledb';"
  if ! local_timescaledb_version="$(query_sql postgres "$version_query" 2>&1)"; then
    case "$local_timescaledb_version" in
      *'could not access file "$libdir/timescaledb-'*'No such file or directory'*)
        case "$pgdata_dir" in
          "$script_dir/local-dev/postgres-data/pgdata") ;;
          *) die "refusing to remove unexpected PGDATA path: $pgdata_dir" ;;
        esac

        log "Local PGDATA references a TimescaleDB version that is not available in the pinned image; recreating local PGDATA"
        run "${compose[@]}" stop "$DB_SERVICE"
        run "${compose[@]}" rm -f "$DB_SERVICE"
        run rm -rf "$pgdata_dir"
        run "${compose[@]}" up -d "$DB_SERVICE"
        db_container="$("${compose[@]}" ps -q "$DB_SERVICE")"
        [ -n "$db_container" ] || die "could not find a running $DB_SERVICE container"
        wait_for_postgres
        local_timescaledb_version="$(query_sql postgres "$version_query")"
        ;;
      *)
        echo "$local_timescaledb_version" >&2
        exit 1
        ;;
    esac
  fi

  if [ "$local_timescaledb_version" != "$EXPECTED_TIMESCALEDB_VERSION" ]; then
    die "local TimescaleDB is $local_timescaledb_version, expected $EXPECTED_TIMESCALEDB_VERSION. Recreate the local database container after updating local-dev/docker-compose.yml."
  fi
fi

log "Dropping and recreating $DB_NAME"
run_sql postgres "DO \$\$ BEGIN CREATE ROLE app; EXCEPTION WHEN duplicate_object THEN NULL; END \$\$;"
run_sql postgres "DROP DATABASE IF EXISTS $DB_NAME WITH (FORCE);"
run_sql postgres "CREATE DATABASE $DB_NAME OWNER $DB_USER;"

log "Preparing TimescaleDB restore state"
run_sql "$DB_NAME" "CREATE EXTENSION IF NOT EXISTS timescaledb;"
run_sql "$DB_NAME" "SELECT timescaledb_pre_restore();"
restore_started=1

log "Restoring $dump_path into $DB_NAME"
if command -v pv >/dev/null 2>&1; then
  echo "+ pv $(printf "%q" "$dump_path") | docker exec -i $db_container pg_restore --exit-on-error --no-acl --no-owner -U $DB_USER -d $DB_NAME -vv"
  if [ "$dry_run" -eq 0 ]; then
    pv "$dump_path" | docker exec -i "$db_container" \
      pg_restore --exit-on-error --no-acl --no-owner -U "$DB_USER" -d "$DB_NAME" -vv
  fi
else
  echo "+ cat $(printf "%q" "$dump_path") | docker exec -i $db_container pg_restore --exit-on-error --no-acl --no-owner -U $DB_USER -d $DB_NAME -vv"
  if [ "$dry_run" -eq 0 ]; then
    cat "$dump_path" | docker exec -i "$db_container" \
      pg_restore --exit-on-error --no-acl --no-owner -U "$DB_USER" -d "$DB_NAME" -vv
  fi
fi

log "Returning TimescaleDB to normal restore state"
run_sql "$DB_NAME" "SELECT timescaledb_post_restore();"
restore_started=0

log "Refreshing planner statistics"
run_sql "$DB_NAME" "ANALYZE;"

log "Local database restore complete"
cat <<EOF

Connection info:
  host:     127.0.0.1
  port:     15432
  database: $DB_NAME
  user:     $DB_USER
  password: local_dev_password
EOF
