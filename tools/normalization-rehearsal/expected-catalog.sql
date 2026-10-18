-- Export one immutable comparison input from a local rehearsal copy at migration49 or later.
-- IDs are retained for ownership checks. Credit prose and artist configuration are excluded.
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'UTC';

WITH artist_rows AS (
    SELECT a.id,a.uuid,a.name,a.slug,a.featured,
        (SELECT count(*) FROM shows sh WHERE sh.artist_id=a.id
            AND EXISTS(SELECT 1 FROM sources s WHERE s.show_id=sh.id)) AS show_count,
        COALESCE((SELECT sum(i.source_count) FROM show_source_information i
            WHERE i.artist_id=a.id),0) AS source_count
    FROM artists a
), show_rows AS (
    SELECT sh.id,sh.uuid,sh.artist_id,a.uuid AS artist_uuid,sh.display_date,
        to_char(sh.date,'YYYY-MM-DD') AS date,sh.performance_type,sh.performance_label,
        sh.is_legacy_survivor,i.show_id IS NOT NULL AS browsable,
        COALESCE(i.source_count,0) AS source_count
    FROM shows sh LEFT JOIN artists a ON a.id=sh.artist_id
    LEFT JOIN show_source_information i ON i.show_id=sh.id
), source_rows AS (
    SELECT s.id,s.uuid,s.artist_id,a.uuid AS artist_uuid,s.show_id,sh.uuid AS show_uuid,
        s.display_date,s.upstream_identifier
    FROM sources s LEFT JOIN artists a ON a.id=s.artist_id LEFT JOIN shows sh ON sh.id=s.show_id
), set_rows AS (
    SELECT ss.id,ss.uuid,ss.source_id,s.uuid AS source_uuid,ss.index,ss.name,ss.is_encore
    FROM source_sets ss LEFT JOIN sources s ON s.id=ss.source_id
), track_rows AS (
    SELECT t.id,t.uuid,t.source_id,s.uuid AS source_uuid,t.source_set_id,ss.uuid AS source_set_uuid,
        t.artist_id,a.uuid AS artist_uuid,ss.source_id AS set_source_id,t.is_orphaned,
        t.track_position,t.title,t.duration,t.mp3_url,t.flac_url
    FROM source_tracks t LEFT JOIN sources s ON s.id=t.source_id
    LEFT JOIN source_sets ss ON ss.id=t.source_set_id LEFT JOIN artists a ON a.id=t.artist_id
), playable AS (
    SELECT p.* FROM collection_playable_sources p JOIN collections c ON c.uuid=p.collection_uuid
    WHERE c.published
), collection_rows AS (
    SELECT c.uuid,c.slug,c.name,c.published,
        (SELECT count(*) FROM playable p WHERE p.collection_uuid=c.uuid) AS source_count,
        (SELECT count(DISTINCT p.show_uuid) FROM playable p WHERE p.collection_uuid=c.uuid) AS show_count,
        (SELECT count(DISTINCT p.artist_uuid) FROM playable p WHERE p.collection_uuid=c.uuid) AS artist_count,
        COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.date DESC,r.source_uuid) FROM (
            SELECT p.source_uuid,p.artist_uuid,p.artist_name,p.artist_slug,p.show_uuid,
                to_char(p.show_date,'YYYY-MM-DD') AS date,p.display_date,p.track_count,p.duration
            FROM playable p WHERE p.collection_uuid=c.uuid
        ) r),'[]'::jsonb) AS recordings,
        COALESCE((SELECT jsonb_agg(to_jsonb(f) ORDER BY f.sort_name COLLATE "C",f.uuid) FROM (
            SELECT p.artist_uuid AS uuid,p.artist_name AS name,p.artist_slug AS slug,
                p.artist_sort_name AS sort_name,count(*) AS source_count,
                count(DISTINCT p.show_uuid) AS show_count,
                EXISTS(SELECT 1 FROM normalization.collection_artist_memberships m
                    WHERE m.collection_uuid=c.uuid AND m.artist_uuid=p.artist_uuid AND m.active)
                    AS includes_all_recordings
            FROM playable p WHERE p.collection_uuid=c.uuid
            GROUP BY p.artist_uuid,p.artist_name,p.artist_slug,p.artist_sort_name
        ) f),'[]'::jsonb) AS artists
    FROM collections c WHERE c.published
)
SELECT jsonb_build_object(
    'schema_version','normalization-api-rehearsal-input-v1',
    'database',current_database(),
    'transaction_read_only',current_setting('transaction_read_only'),
    'migration',(SELECT max(version) FROM versioninfo),
    'artists',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.uuid) FROM artist_rows a),'[]'::jsonb),
    'shows',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.uuid) FROM show_rows s),'[]'::jsonb),
    'sources',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.uuid) FROM source_rows s),'[]'::jsonb),
    'sets',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.uuid) FROM set_rows s),'[]'::jsonb),
    'tracks',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.uuid) FROM track_rows t),'[]'::jsonb),
    'collections',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.slug COLLATE "C",c.uuid)
        FROM collection_rows c),'[]'::jsonb),
    'normalization_state',jsonb_build_object(
        'people',(SELECT count(*) FROM normalization.people),
        'venues',(SELECT count(*) FROM normalization.canonical_venues),
        'compositions',(SELECT count(*) FROM normalization.compositions),
        'repertoire_songs',(SELECT count(*) FROM normalization.repertoire_songs),
        'recording_credits',(SELECT count(*) FROM normalization.recording_credits),
        'imported_venue_assignments',(SELECT count(*) FROM normalization.imported_venue_assignments),
        'show_venue_assignments',(SELECT count(*) FROM normalization.show_venue_assignments),
        'track_contents',(SELECT count(*) FROM normalization.track_contents),
        'setlist_editions',(SELECT count(*) FROM normalization.setlist_editions),
        'song_occurrences',(SELECT count(*) FROM normalization.song_occurrences),
        'setlist_structures',(SELECT count(*) FROM normalization.setlist_structures),
        'setlist_sets',(SELECT count(*) FROM normalization.setlist_sets),
        'music_exclusions',(SELECT count(*) FROM normalization.music_exclusions),
        'active_people_policies',(SELECT count(*) FROM normalization.people_acceptance_active
            WHERE artifact_uuid IS NOT NULL),
        'active_composition_policies',(SELECT count(*) FROM normalization.composition_acceptance_active
            WHERE artifact_uuid IS NOT NULL)
    )
);
COMMIT;
