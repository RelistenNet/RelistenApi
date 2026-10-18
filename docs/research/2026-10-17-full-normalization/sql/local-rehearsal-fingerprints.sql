-- Compare complete application rows before and after a local restore or migration.
-- Hash original rows inside PostgreSQL so recording prose and contact values do
-- not enter the receipt. These hashes verify copied data, not media availability.
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'UTC';

WITH table_names(name) AS (
    VALUES ('artists'), ('shows'), ('sources'), ('source_sets'),
           ('source_tracks'), ('collections'), ('collection_playable_sources')
), application_rows(name, contents) AS (
    SELECT 'artists', to_jsonb(a) FROM artists a
    UNION ALL SELECT 'shows', to_jsonb(s) FROM shows s
    UNION ALL SELECT 'sources', to_jsonb(s) FROM sources s
    UNION ALL SELECT 'source_sets', to_jsonb(s) FROM source_sets s
    UNION ALL SELECT 'source_tracks', to_jsonb(t) FROM source_tracks t
    UNION ALL SELECT 'collections', to_jsonb(c) FROM collections c
    UNION ALL SELECT 'collection_playable_sources', to_jsonb(p) FROM collection_playable_sources p
), fingerprints AS (
    SELECT n.name,
           count(r.contents) AS rows,
           encode(sha256(convert_to(coalesce(
               string_agg(r.contents::text, E'\n' ORDER BY r.contents::text COLLATE "C"),
               ''
           ), 'UTF8')), 'hex') AS sha256
    FROM table_names n
    LEFT JOIN application_rows r ON r.name = n.name
    GROUP BY n.name
)
SELECT jsonb_build_object(
    'database', current_database(),
    'observed_at', clock_timestamp(),
    'transaction_read_only', current_setting('transaction_read_only'),
    'timescaledb_version', (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'),
    'application_rows', (SELECT jsonb_object_agg(name, jsonb_build_object('rows', rows, 'sha256', sha256)) FROM fingerprints)
);

COMMIT;
