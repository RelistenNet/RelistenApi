\timing on
BEGIN READ ONLY;
SET LOCAL statement_timeout = '10s';
SELECT current_timestamp AS measured_at;
SELECT count(*) AS tracks, count(*) FILTER (WHERE is_orphaned IS NOT TRUE) AS non_orphaned_tracks FROM source_tracks;
SELECT relname, reltuples::bigint AS planner_estimate, pg_total_relation_size(oid) AS table_and_index_bytes
FROM pg_class WHERE relnamespace='public'::regnamespace AND relname IN ('sources','shows','source_tracks');
SELECT count(*) FILTER (WHERE upstream_source_id=1) AS archive_mappings, count(*) AS all_mappings FROM artists_upstream_sources;
SELECT count(*) AS artists_with_sources FROM (SELECT distinct artist_id FROM sources) x;
COMMIT;
