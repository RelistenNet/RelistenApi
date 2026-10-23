#!/usr/bin/env bash
# Builds relisten_norm_lab: a local copy of a few real artists from the production read-only
# replica, migrated to the latest schema, so the norm song catalogue can be exercised on real
# data before its schema is ratified for production. Production is only read
# (default_transaction_read_only); everything is written to the local lab database.
#
# Usage: ./local-dev/norm-lab.sh [--reset] SLUG...
#   --reset   drop and recreate relisten_norm_lab first
# Reloading an artist replaces its rows. Needs psql (libpq) on PATH or in PSQL, kubectl
# access to the relisten3 context (KUBE_CONTEXT), and the local services running.

set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_dir="$(dirname "$script_dir")"
readonly LAB_DB="relisten_norm_lab"
readonly KUBE_CONTEXT="${KUBE_CONTEXT:-relisten3}"
readonly PROD_HOST="relisten-db-ro.tail09dbf.ts.net"
PSQL="${PSQL:-$(command -v psql || echo /opt/homebrew/opt/libpq/bin/psql)}"

reset=0
slugs=()
for arg in "$@"; do
  case "$arg" in
    --reset) reset=1 ;;
    -h | --help) sed -n '2,12p' "$0"; exit 0 ;;
    *) slugs+=("$arg") ;;
  esac
done
[[ ${#slugs[@]} -gt 0 ]] || { echo "usage: $0 [--reset] SLUG..." >&2; exit 2; }

db_container="$(docker compose -f "$script_dir/docker-compose.yml" ps -q relisten-db)"
[[ -n "$db_container" ]] || { echo "error: start the local services first (./start-local-databases.sh)" >&2; exit 1; }

local_psql() {
  docker exec -i "$db_container" psql -X -q -v ON_ERROR_STOP=1 -U relisten "$@"
}

prod_psql() {
  PGPASSWORD="$(kubectl --context "$KUBE_CONTEXT" -n default get secret relisten-db-app -o jsonpath='{.data.password}' | base64 --decode)" \
    PGSSLMODE=require PGOPTIONS='-c default_transaction_read_only=on' \
    "$PSQL" -X -q -v ON_ERROR_STOP=1 -h "$PROD_HOST" -p 5432 -U app -d app "$@"
}

# 1. The lab database, at the catalog schema the seed has (migration 12).
if [[ $reset -eq 1 ]]; then
  local_psql -d postgres -c "DROP DATABASE IF EXISTS $LAB_DB WITH (FORCE)"
fi
if [[ -z "$(local_psql -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname = '$LAB_DB'")" ]]; then
  echo "==> creating $LAB_DB"
  local_psql -d postgres -c "CREATE DATABASE $LAB_DB TEMPLATE template0"
  local_psql -d "$LAB_DB" < "$script_dir/seed/schema.sql" > /dev/null
fi

# 2. The artists' rows. Each table's filter selects one artist's evidence; columns are those
# the local and production schemas share.
slug_list="$(printf "'%s'," "${slugs[@]}")"
ids="$(prod_psql -Atc "SELECT string_agg(id::text, ',') FROM artists WHERE slug IN (${slug_list%,})")"
[[ -n "$ids" ]] || { echo "error: no artists match ${slugs[*]}" >&2; exit 1; }

tables=(artists features venues tours eras shows sources source_sets source_tracks setlist_songs setlist_shows setlist_songs_plays)
filter() {
  case "$1" in
    artists) echo "id IN ($ids)" ;;
    venues) echo "artist_id IN ($ids) OR id IN (SELECT venue_id FROM shows WHERE artist_id IN ($ids) UNION SELECT venue_id FROM sources WHERE artist_id IN ($ids) UNION SELECT venue_id FROM setlist_shows WHERE artist_id IN ($ids))" ;;
    source_sets) echo "source_id IN (SELECT id FROM sources WHERE artist_id IN ($ids))" ;;
    setlist_songs_plays) echo "played_setlist_show_id IN (SELECT id FROM setlist_shows WHERE artist_id IN ($ids))" ;;
    *) echo "artist_id IN ($ids)" ;;
  esac
}

# Approvals are the lab's own decisions: keep them across a reload of the artist's rows.
approved="$(local_psql -d "$LAB_DB" -Atc "SELECT coalesce(string_agg(artist_id::text, ','), '') FROM features WHERE artist_id IN ($ids) AND norm_catalogue" 2>/dev/null || true)"

# Foreign keys and triggers stay off while rows arrive out of order (session_replication_role).
for ((i = ${#tables[@]} - 1; i >= 0; i--)); do
  t="${tables[$i]}"
  local_psql -d "$LAB_DB" -c "SET session_replication_role = replica" -c "DELETE FROM public.$t WHERE $(filter "$t")" > /dev/null
done
for t in "${tables[@]}"; do
  # The columns both sides have: once migrated, the lab has columns production doesn't yet
  # (features.norm_catalogue); those keep their defaults.
  local_cols="$(local_psql -d "$LAB_DB" -Atc "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '$t' AND is_generated = 'NEVER' ORDER BY ordinal_position")"
  prod_cols="$(prod_psql -Atc "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '$t'")"
  cols="$(grep -Fxf <(echo "$prod_cols") <(echo "$local_cols") | sed 's/.*/"&"/' | paste -sd, -)"
  echo "==> $t"
  prod_psql -c "\\copy (SELECT $cols FROM public.$t WHERE $(filter "$t")) TO STDOUT" |
    local_psql -d "$LAB_DB" -c "SET session_replication_role = replica" -c "\\copy public.$t ($cols) FROM STDIN"
done

if [[ -n "$approved" ]]; then
  local_psql -d "$LAB_DB" -c "UPDATE features SET norm_catalogue = true WHERE artist_id IN ($approved)" > /dev/null
fi

# Sequences past the copied ids, so local inserts don't collide.
local_psql -d "$LAB_DB" -Atc "
  SELECT format('SELECT setval(%L, coalesce((SELECT max(%I) FROM %I.%I), 1));', s.seq, a.attname, n.nspname, c.relname)
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0
  JOIN LATERAL (SELECT pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname) AS seq) s ON s.seq IS NOT NULL
  WHERE n.nspname = 'public' AND c.relkind = 'r'" | local_psql -d "$LAB_DB" > /dev/null

# 3. The latest migrations, as the worker applies them.
echo "==> migrating"
DATABASE_URL="postgresql://relisten:local_dev_password@127.0.0.1:15432/$LAB_DB" \
  Ingestion__LocalSchemaBaseline=12 \
  dotnet run --project "$repo_dir/RelistenIngestionWorker" -- --migrate

local_psql -d "$LAB_DB" -c "
  SELECT a.slug, count(DISTINCT s.id) AS sources, count(t.id) AS tracks
  FROM artists a LEFT JOIN sources s ON s.artist_id = a.id LEFT JOIN source_tracks t ON t.source_id = s.id
  GROUP BY a.slug ORDER BY tracks DESC"
