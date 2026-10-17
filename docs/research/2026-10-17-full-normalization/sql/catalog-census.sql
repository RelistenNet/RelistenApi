-- Full imported-catalog census. One repeatable-read snapshot, no artist sample.
-- Nonempty fields and lexical patterns measure available evidence, not identity accuracy.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

SELECT jsonb_build_object(
    'kind', 'snapshot',
    'captured_at', now(),
    'read_only', current_setting('transaction_read_only'),
    'in_recovery', pg_is_in_recovery(),
    'replay_lsn', pg_last_wal_replay_lsn(),
    'snapshot', pg_current_snapshot()::text,
    'migration_versions', (SELECT jsonb_agg(version ORDER BY version) FROM versioninfo)
);

SELECT jsonb_build_object('kind', 'schema', 'rows', jsonb_agg(x))
FROM (
    SELECT table_schema, table_name, column_name, data_type, is_nullable
    FROM information_schema.columns
    WHERE table_schema IN ('public', 'normalization')
      AND table_name IN (
          'artists', 'sources', 'source_sets', 'source_tracks', 'shows', 'venues',
          'setlist_songs', 'setlist_shows', 'setlist_songs_plays',
          'artists_upstream_sources', 'upstream_sources', 'archive_item_observations',
          'source_show_assignments', 'show_performance_keys'
      )
    ORDER BY table_schema, table_name, ordinal_position
) x;

WITH
recordings AS (
    SELECT artist_id, count(*) AS sources,
        count(*) FILTER (WHERE nullif(btrim(taper), '') IS NOT NULL) AS with_taper_text,
        count(*) FILTER (WHERE nullif(btrim(transferrer), '') IS NOT NULL) AS with_transferrer_text,
        count(*) FILTER (WHERE nullif(btrim(lineage), '') IS NOT NULL) AS with_lineage_text,
        count(*) FILTER (WHERE nullif(btrim(source), '') IS NOT NULL) AS with_source_text,
        count(*) FILTER (WHERE nullif(btrim(taper_notes), '') IS NOT NULL) AS with_notes_text,
        count(*) FILTER (WHERE nullif(btrim(description), '') IS NOT NULL) AS with_description,
        count(*) FILTER (WHERE nullif(btrim(lineage), '') IS NOT NULL
            OR nullif(btrim(source), '') IS NOT NULL) AS with_lineage_or_source_text,
        count(*) FILTER (WHERE show_id IS NULL) AS without_show,
        count(*) FILTER (WHERE display_date ~* 'x') AS partial_date_sources,
        min(display_date) AS first_display_date, max(display_date) AS last_display_date
    FROM sources GROUP BY artist_id
), files AS (
    SELECT t.artist_id, count(*) AS tracks_total,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE) AS active_tracks,
        count(*) FILTER (WHERE t.is_orphaned IS TRUE) AS orphaned_tracks,
        count(*) FILTER (WHERE t.is_orphaned IS NULL) AS unknown_orphan_state,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND nullif(btrim(t.title), '') IS NULL) AS active_missing_title,
        count(DISTINCT lower(regexp_replace(btrim(t.title), '\s+', ' ', 'g')))
            FILTER (WHERE t.is_orphaned IS FALSE AND nullif(btrim(t.title), '') IS NOT NULL) AS active_title_keys,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND t.title ~ '>|→|»') AS compound_title_candidates,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND t.mp3_md5 ~* '^[0-9a-f]{32}$') AS with_mp3_md5,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND t.flac_md5 ~* '^[0-9a-f]{32}$') AS with_flac_md5,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND t.duration > 0) AS with_positive_duration,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND s.id IS NULL) AS active_without_source,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND s.id IS NOT NULL
            AND s.artist_id IS DISTINCT FROM t.artist_id) AS active_source_artist_mismatch,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE
            AND (st.id IS NULL OR st.source_id IS DISTINCT FROM t.source_id)) AS active_without_matching_set,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE
            AND nullif(btrim(t.mp3_url), '') IS NULL
            AND nullif(btrim(t.flac_url), '') IS NULL) AS active_without_media_reference,
        count(*) FILTER (WHERE t.is_orphaned IS FALSE AND s.artist_id=t.artist_id
            AND sh.artist_id=s.artist_id AND st.source_id=s.id
            AND (nullif(btrim(t.mp3_url), '') IS NOT NULL
                OR nullif(btrim(t.flac_url), '') IS NOT NULL)) AS active_with_catalog_playback_links
    FROM source_tracks t
    LEFT JOIN sources s ON s.id=t.source_id
    LEFT JOIN source_sets st ON st.id=t.source_set_id
    LEFT JOIN shows sh ON sh.id=s.show_id
    GROUP BY t.artist_id
), repertoire AS (
    SELECT artist_id, count(*) AS song_rows,
        count(DISTINCT lower(regexp_replace(btrim(name), '\s+', ' ', 'g'))) AS song_title_keys,
        count(*) FILTER (WHERE nullif(btrim(upstream_identifier), '') IS NOT NULL) AS songs_with_provider_identifier
    FROM setlist_songs GROUP BY artist_id
), provider_setlists AS (
    SELECT artist_id, count(*) AS setlist_shows FROM setlist_shows GROUP BY artist_id
), recording_sets AS (
    SELECT s.artist_id, count(*) AS source_sets
    FROM source_sets st LEFT JOIN sources s ON s.id=st.source_id
    GROUP BY s.artist_id
), repertoire_plays AS (
    SELECT s.artist_id, count(*) AS setlist_songs_plays
    FROM setlist_songs_plays p LEFT JOIN setlist_songs s ON s.id=p.played_setlist_song_id
    GROUP BY s.artist_id
), recorded_shows AS (
    SELECT artist_id, count(*) AS shows FROM shows GROUP BY artist_id
), places AS (
    SELECT artist_id, count(*) AS venue_rows,
        count(*) FILTER (WHERE latitude IS NOT NULL AND longitude IS NOT NULL) AS venues_with_coordinates,
        count(*) FILTER (WHERE nullif(btrim(location), '') IS NOT NULL) AS venues_with_location,
        count(*) FILTER (WHERE nullif(btrim(upstream_identifier), '') IS NOT NULL) AS venues_with_provider_identifier,
        count(*) FILTER (WHERE nullif(btrim(past_names), '') IS NOT NULL) AS venues_with_past_names
    FROM venues GROUP BY artist_id
), provider_mappings AS (
    SELECT aus.artist_id, jsonb_agg(jsonb_build_object(
        'provider', u.name, 'upstream_identifier', aus.upstream_identifier
    ) ORDER BY u.name, aus.upstream_identifier) AS providers
    FROM artists_upstream_sources aus JOIN upstream_sources u ON u.id = aus.upstream_source_id
    GROUP BY aus.artist_id
)
SELECT jsonb_build_object('kind', 'artist', 'profile',
    jsonb_build_object('id', a.id, 'uuid', a.uuid, 'name', a.name, 'slug', a.slug,
        'musicbrainz_artist_id', a.musicbrainz_id,
        'providers', coalesce(p.providers, '[]'::jsonb))
    || (coalesce(to_jsonb(r), '{}'::jsonb) - 'artist_id')
    || (coalesce(to_jsonb(f), '{}'::jsonb) - 'artist_id')
    || (coalesce(to_jsonb(s), '{}'::jsonb) - 'artist_id')
    || (coalesce(to_jsonb(ps), '{}'::jsonb) - 'artist_id')
    || (coalesce(to_jsonb(st), '{}'::jsonb) - 'artist_id')
    || (coalesce(to_jsonb(rp), '{}'::jsonb) - 'artist_id')
    || (coalesce(to_jsonb(rs), '{}'::jsonb) - 'artist_id')
    || (coalesce(to_jsonb(v), '{}'::jsonb) - 'artist_id'))
FROM artists a
LEFT JOIN recordings r ON r.artist_id = a.id
LEFT JOIN files f ON f.artist_id = a.id
LEFT JOIN repertoire s ON s.artist_id = a.id
LEFT JOIN provider_setlists ps ON ps.artist_id = a.id
LEFT JOIN recording_sets st ON st.artist_id = a.id
LEFT JOIN repertoire_plays rp ON rp.artist_id = a.id
LEFT JOIN recorded_shows rs ON rs.artist_id = a.id
LEFT JOIN places v ON v.artist_id = a.id
LEFT JOIN provider_mappings p ON p.artist_id = a.id
ORDER BY a.id;

-- Independent totals detect rows omitted by an artist join, including artist-less venues.
SELECT jsonb_build_object('kind', 'totals', 'rows', jsonb_build_object(
    'artists', (SELECT count(*) FROM artists),
    'sources', (SELECT count(*) FROM sources),
    'shows', (SELECT count(*) FROM shows),
    'source_sets', (SELECT count(*) FROM source_sets),
    'source_tracks', (SELECT count(*) FROM source_tracks),
    'venues', (SELECT count(*) FROM venues),
    'setlist_songs', (SELECT count(*) FROM setlist_songs),
    'setlist_shows', (SELECT count(*) FROM setlist_shows),
    'setlist_songs_plays', (SELECT count(*) FROM setlist_songs_plays)
));

SELECT jsonb_build_object('kind', 'unattributed', 'rows', jsonb_build_object(
    'sources', (SELECT count(*) FROM sources s WHERE NOT EXISTS (SELECT 1 FROM artists a WHERE a.id=s.artist_id)),
    'tracks', (SELECT count(*) FROM source_tracks t WHERE NOT EXISTS (SELECT 1 FROM artists a WHERE a.id=t.artist_id)),
    'venues', (SELECT count(*) FROM venues v WHERE NOT EXISTS (SELECT 1 FROM artists a WHERE a.id=v.artist_id)),
    'songs', (SELECT count(*) FROM setlist_songs s WHERE NOT EXISTS (SELECT 1 FROM artists a WHERE a.id=s.artist_id)),
    'shows', (SELECT count(*) FROM shows s WHERE NOT EXISTS (SELECT 1 FROM artists a WHERE a.id=s.artist_id)),
    'setlist_shows', (SELECT count(*) FROM setlist_shows s WHERE NOT EXISTS (SELECT 1 FROM artists a WHERE a.id=s.artist_id))
    ,'source_sets', (SELECT count(*) FROM source_sets st WHERE NOT EXISTS (
        SELECT 1 FROM sources s JOIN artists a ON a.id=s.artist_id WHERE s.id=st.source_id))
    ,'setlist_songs_plays', (SELECT count(*) FROM setlist_songs_plays p WHERE NOT EXISTS (
        SELECT 1 FROM setlist_songs s JOIN artists a ON a.id=s.artist_id WHERE s.id=p.played_setlist_song_id))
));

SELECT jsonb_build_object('kind', 'snapshot_complete', 'completed_at', clock_timestamp());
COMMIT;
