-- Read existing local import rehearsals without migrating or changing them.
-- Collection counts use the public view: selector membership alone does not
-- establish a current catalog source, artist, show or linked media file.
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';

SELECT jsonb_build_object(
    'database', current_database(),
    'observed_at', clock_timestamp(),
    'transaction_read_only', current_setting('transaction_read_only'),
    'artists', (SELECT count(*) FROM artists),
    'sources', (SELECT count(*) FROM sources),
    'shows', (SELECT count(*) FROM shows),
    'normalization_tables', (
        SELECT coalesce(jsonb_agg(table_name ORDER BY table_name), '[]'::jsonb)
        FROM information_schema.tables
        WHERE table_schema = 'normalization'
    ),
    'largest_recording_artists', (
        SELECT coalesce(jsonb_agg(to_jsonb(population) ORDER BY sources DESC, slug), '[]'::jsonb)
        FROM (
            SELECT a.uuid, a.slug, a.name, count(s.id) AS sources
            FROM artists a
            JOIN sources s ON s.artist_id = a.id
            GROUP BY a.id, a.uuid, a.slug, a.name
            ORDER BY sources DESC, a.slug
            LIMIT 8
        ) population
    ),
    'collections', (
        SELECT coalesce(jsonb_agg(to_jsonb(population) ORDER BY slug), '[]'::jsonb)
        FROM (
            SELECT c.uuid, c.slug, c.name,
                count(p.source_uuid) AS catalog_linked_recordings,
                count(DISTINCT p.artist_uuid) AS artists
            FROM collections c
            LEFT JOIN collection_playable_sources p ON p.collection_uuid = c.uuid
            GROUP BY c.uuid, c.slug, c.name
        ) population
    )
);

COMMIT;
