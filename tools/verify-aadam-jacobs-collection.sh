#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:5000}"
DELTA_SINCE="${DELTA_SINCE:-1970-01-01T00%3A00%3A00Z}"
CURL_BIN="${CURL_BIN:-curl}"
JQ_BIN="${JQ_BIN:-jq}"
PSQL_BIN="${PSQL_BIN:-psql}"

require_tool() {
    local tool="$1"

    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Missing required tool: $tool" >&2
        exit 1
    fi
}

api_check() {
    local label="$1"
    local url="$2"
    local jq_filter="$3"

    printf "\n== %s ==\n" "$label"
    "$CURL_BIN" -fsS "$url" | "$JQ_BIN" "$jq_filter"
}

run_psql_checks() {
    if ! has_database_connection; then
        cat <<'NOTE'

Skipping SQL checks because DATABASE_URL or PG* connection variables are not set.
Set DATABASE_URL, or set PGHOST/PGPORT/PGDATABASE/PGUSER/PGPASSWORD, then rerun this script.
NOTE
        return
    fi

    psql_with_connection <<'SQL'
select
    c.item_count,
    c.indexed_at,
    count(distinct i.upstream_identifier) filter (where i.removed_at is null) as active_distinct_items,
    count(*) filter (where i.removed_at is not null) as removed_items,
    count(*) filter (where i.removed_at is null and i.import_status = 1) as linked_existing_source,
    count(*) filter (where i.removed_at is null and i.import_status = 2) as imported_source,
    count(*) filter (where i.removed_at is null and i.import_status = 3) as skipped,
    count(*) filter (where i.removed_at is null and i.import_status = 4) as import_error,
    count(*) filter (where i.removed_at is null and i.source_uuid is not null and (i.artist_uuid is null or i.show_uuid is null)) as broken_links
from collections c
join collection_items i on i.collection_uuid = c.uuid
where c.slug = 'aadam-jacobs'
group by c.uuid;
SQL
}

has_database_connection() {
    [[ -n "${DATABASE_URL:-}" || -n "${PGHOST:-}" || -n "${PGDATABASE:-}" ]]
}

psql_with_connection() {
    if [[ -n "${DATABASE_URL:-}" ]]; then
        "$PSQL_BIN" "$DATABASE_URL" -v ON_ERROR_STOP=1
    else
        "$PSQL_BIN" -v ON_ERROR_STOP=1
    fi
}

run_explain_checks() {
    if [[ "${RUN_EXPLAIN:-0}" != "1" ]]; then
        cat <<'NOTE'

Skipping EXPLAIN checks. Set RUN_EXPLAIN=1 with a staging database connection to run them.
NOTE
        return
    fi

    if ! has_database_connection; then
        echo "Skipping EXPLAIN checks because no database connection variables are set."
        return
    fi

    psql_with_connection <<'SQL'
EXPLAIN (ANALYZE, BUFFERS)
select
    a.uuid,
    a.name,
    count(distinct ci.show_uuid) as show_count,
    count(distinct ci.source_uuid) as source_count
from collection_items ci
join collections c on c.uuid = ci.collection_uuid
join artists a on a.uuid = ci.artist_uuid
where c.slug = 'aadam-jacobs'
  and ci.removed_at is null
  and ci.source_uuid is not null
group by a.id
order by a.sort_name, a.name;

EXPLAIN (ANALYZE, BUFFERS)
select *
from collection_years cy
join collections c on c.uuid = cy.collection_uuid
where c.slug = 'aadam-jacobs'
order by cy.year;

EXPLAIN (ANALYZE, BUFFERS)
with collection_show_info as (
    select
        ci.show_uuid,
        count(distinct ci.source_uuid) as source_count
    from collection_items ci
    join collections c on c.uuid = ci.collection_uuid
    where c.slug = 'aadam-jacobs'
      and ci.removed_at is null
      and ci.show_uuid is not null
      and ci.source_uuid is not null
    group by ci.show_uuid
)
select sh.uuid, sh.artist_id, sh.display_date, csi.source_count
from collection_show_info csi
join shows sh on sh.uuid = csi.show_uuid
where extract(month from sh.date) = 1
  and extract(day from sh.date) = 1
order by sh.display_date;

EXPLAIN (ANALYZE, BUFFERS)
with collection_sources as (
    select distinct ci.source_uuid
    from collection_items ci
    join collections c on c.uuid = ci.collection_uuid
    where c.slug = 'aadam-jacobs'
      and ci.removed_at is null
      and ci.source_uuid is not null
)
select p.show_uuid, sum(p.plays) as plays_30d
from source_track_plays_daily p
join collection_sources cs on cs.source_uuid = p.source_uuid
where p.play_day >= now() - interval '30 days'
group by p.show_uuid
order by plays_30d desc
limit 25;
SQL
}

cat <<'NOTE'
Manual staging prerequisite: Run the AJC import job twice before using this script for rollout signoff.
Compare source counts between runs and confirm the second run does not create duplicate rows or delete non-AJC sources.
NOTE

require_tool "$CURL_BIN"
require_tool "$JQ_BIN"

api_check "collection list keys" \
    "$BASE_URL/api/v3/collections" \
    '.[0] | keys'

api_check "collection detail counts" \
    "$BASE_URL/api/v3/collections/aadam-jacobs" \
    '{uuid, slug, item_count, indexed_at, artist_count, show_count, source_count}'

api_check "collection artists count" \
    "$BASE_URL/api/v3/collections/aadam-jacobs/artists" \
    'length'

api_check "collection first year shape" \
    "$BASE_URL/api/v3/collections/aadam-jacobs/years" \
    '.[0] | {uuid, collection_uuid, year, artist_count, show_count, source_count}'

api_check "collection popular and trending counts" \
    "$BASE_URL/api/v3/collections/aadam-jacobs/shows/popular-trending" \
    '{popular: (.popular_shows | length), trending: (.trending_shows | length)}'

api_check "collection on-this-day first show" \
    "$BASE_URL/api/v3/collections/aadam-jacobs/shows/on-this-day?month=1&day=1" \
    '.[0] | {uuid, artist_uuid, display_date}'

api_check "collection-derived artist delta shape" \
    "$BASE_URL/api/v3/artists/delta?since=$DELTA_SINCE&include_collection_derived=true" \
    '{server_timestamp, artist_count: (.artists | length)}'

if command -v "$PSQL_BIN" >/dev/null 2>&1; then
    run_psql_checks
    run_explain_checks
else
    echo "Skipping SQL checks because psql is not installed."
fi
