-- Full-domain metadata only. Names, titles, URLs and prose never leave the server.
-- MD5/length profiles do not establish entity identity or evaluation independence.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL TIME ZONE 'UTC';
SET LOCAL work_mem='16MB';
SELECT jsonb_build_object(
    'kind','metadata_snapshot','version','full-domain-metadata-frame-v1',
    'captured_at',transaction_timestamp(),'snapshot',pg_current_snapshot()::text,
    'read_only',current_setting('transaction_read_only'),
    'isolation',current_setting('transaction_isolation'),
    'server_version',current_setting('server_version'),
    'server_encoding',current_setting('server_encoding'),'client_encoding',current_setting('client_encoding'),
    'in_recovery',pg_is_in_recovery(),'replay_lsn',pg_last_wal_replay_lsn(),
    'last_replayed_transaction_at',pg_last_xact_replay_timestamp(),
    'provider_attribution','unavailable','date_precision','unavailable','knowledge_clock','unavailable',
    'text_profile_algorithm','md5_utf8_not_identity_proof',
    'content_derived_metadata_computed',true,'server_source_processing','nullness_length_md5_only',
    'semantic_parsing',false,'performance_interpretation','unavailable',
    'play_association_interpretation','unordered_multiset','exposure_history','unavailable');

SELECT jsonb_build_object('kind','metadata_counts',
    'artists',(SELECT count(*) FROM public.artists),
    'sources',(SELECT count(*) FROM public.sources),
    'shows',(SELECT count(*) FROM public.shows),
    'venues',(SELECT count(*) FROM public.venues),
    'source_sets',(SELECT count(*) FROM public.source_sets),
    'source_tracks',(SELECT count(*) FROM public.source_tracks),
    'setlist_shows',(SELECT count(*) FROM public.setlist_shows),
    'setlist_songs',(SELECT count(*) FROM public.setlist_songs),
    'setlist_songs_plays',(SELECT count(*) FROM public.setlist_songs_plays),
    'setlist_song_play_groups',(SELECT count(*) FROM (
        SELECT played_setlist_song_id,played_setlist_show_id FROM public.setlist_songs_plays
        GROUP BY played_setlist_song_id,played_setlist_show_id) grouped));

SELECT jsonb_build_object('kind','metadata_columns','table_name',table_name,
    'column_name',column_name,'data_type',data_type,'is_nullable',is_nullable)
FROM information_schema.columns
WHERE table_schema='public' AND table_name IN
    ('artists','sources','shows','venues','source_sets','source_tracks',
     'setlist_shows','setlist_songs','setlist_songs_plays')
ORDER BY table_name,ordinal_position;

-- Minimal current parent rosters are captured again inside this snapshot.
SELECT jsonb_build_object(
    'kind','artist',
    'id',a.id,
    'uuid',a.uuid,
    'created_at',a.created_at,
    'updated_at',a.updated_at)
FROM public.artists a
ORDER BY a.id;

SELECT jsonb_build_object(
    'kind','source',
    'id',s.id,
    'uuid',s.uuid,
    'artist_id',s.artist_id,
    'artist_uuid',a.uuid,
    'show_id',s.show_id,
    'show_uuid',sh.uuid,
    'venue_id',s.venue_id,
    'venue_uuid',v.uuid,
    'created_at',s.created_at,
    'updated_at',s.updated_at)
FROM public.sources s
LEFT JOIN public.artists a ON a.id=s.artist_id
LEFT JOIN public.shows sh ON sh.id=s.show_id
LEFT JOIN public.venues v ON v.id=s.venue_id
ORDER BY s.id;

SELECT jsonb_build_object(
    'kind','show',
    'id',sh.id,
    'uuid',sh.uuid,
    'artist_id',sh.artist_id,
    'artist_uuid',a.uuid,
    'venue_id',sh.venue_id,
    'venue_uuid',v.uuid,
    'catalog_sort_date',sh.date,
    'event_date_precision',NULL,
    'created_at',sh.created_at,
    'updated_at',sh.updated_at)
FROM public.shows sh
LEFT JOIN public.artists a ON a.id=sh.artist_id
LEFT JOIN public.venues v ON v.id=sh.venue_id
ORDER BY sh.id;

SELECT jsonb_build_object(
    'kind','venue',
    'id',v.id,
    'uuid',v.uuid,
    'artist_id',v.artist_id,
    'artist_uuid',a.uuid,
    'latitude',v.latitude,
    'longitude',v.longitude,
    'created_at',v.created_at,
    'updated_at',v.updated_at,
    'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
        'is_null',p.value IS NULL,'characters',char_length(p.value),
        'utf8_bytes',octet_length(p.value),'md5_utf8',md5(p.value)))
        FROM (VALUES ('name',v.name),
            ('location',v.location),
            ('upstream_identifier',v.upstream_identifier),
            ('slug',v.slug),
            ('past_names',v.past_names)) AS p(field,value)))
FROM public.venues v
LEFT JOIN public.artists a ON a.id=v.artist_id
ORDER BY v.id;

-- Stored set index/name/encore do not identify a performance.
SELECT jsonb_build_object(
    'kind','source_set',
    'id',st.id,
    'uuid',st.uuid,
    'source_id',st.source_id,
    'source_uuid',s.uuid,
    'index',st.index,
    'is_encore',st.is_encore,
    'created_at',st.created_at,
    'updated_at',st.updated_at,
    'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
        'is_null',p.value IS NULL,'characters',char_length(p.value),
        'utf8_bytes',octet_length(p.value),'md5_utf8',md5(p.value)))
        FROM (VALUES ('name',st.name)) AS p(field,value)))
FROM public.source_sets st
LEFT JOIN public.sources s ON s.id=st.source_id
ORDER BY st.id;

-- Preserve direct and set pointers independently, including absent/dangling parents.
SELECT jsonb_build_object(
    'kind','source_track',
    'id',t.id,
    'uuid',t.uuid,
    'artist_id',t.artist_id,
    'artist_uuid',a.uuid,
    'source_id',t.source_id,
    'source_uuid',s.uuid,
    'source_set_id',t.source_set_id,
    'source_set_uuid',st.uuid,
    'track_position',t.track_position,
    'duration',t.duration,
    'is_orphaned',t.is_orphaned,
    'created_at',t.created_at,
    'updated_at',t.updated_at,
    'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
        'is_null',p.value IS NULL,'characters',char_length(p.value),
        'utf8_bytes',octet_length(p.value),'md5_utf8',md5(p.value)))
        FROM (VALUES ('title',t.title),
            ('slug',t.slug),
            ('mp3_url',t.mp3_url),
            ('mp3_md5',t.mp3_md5),
            ('flac_md5',t.flac_md5),
            ('flac_url',t.flac_url)) AS p(field,value)))
FROM public.source_tracks t
LEFT JOIN public.artists a ON a.id=t.artist_id
LEFT JOIN public.sources s ON s.id=t.source_id
LEFT JOIN public.source_sets st ON st.id=t.source_set_id
ORDER BY t.id;

SELECT jsonb_build_object(
    'kind','setlist_show',
    'id',sh.id,
    'uuid',sh.uuid,
    'artist_id',sh.artist_id,
    'artist_uuid',a.uuid,
    'venue_id',sh.venue_id,
    'venue_uuid',v.uuid,
    'tour_id',sh.tour_id,
    'era_id',sh.era_id,
    'catalog_sort_date',sh.date,
    'event_date_precision',NULL,
    'created_at',sh.created_at,
    'updated_at',sh.updated_at,
    'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
        'is_null',p.value IS NULL,'characters',char_length(p.value),
        'utf8_bytes',octet_length(p.value),'md5_utf8',md5(p.value)))
        FROM (VALUES ('upstream_identifier',sh.upstream_identifier)) AS p(field,value)))
FROM public.setlist_shows sh
LEFT JOIN public.artists a ON a.id=sh.artist_id
LEFT JOIN public.venues v ON v.id=sh.venue_id
ORDER BY sh.id;

SELECT jsonb_build_object(
    'kind','setlist_song',
    'id',song.id,
    'uuid',song.uuid,
    'artist_id',song.artist_id,
    'artist_uuid',a.uuid,
    'created_at',song.created_at,
    'updated_at',song.updated_at,
    'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
        'is_null',p.value IS NULL,'characters',char_length(p.value),
        'utf8_bytes',octet_length(p.value),'md5_utf8',md5(p.value)))
        FROM (VALUES ('name',song.name),
            ('slug',song.slug),
            ('upstream_identifier',song.upstream_identifier)) AS p(field,value)))
FROM public.setlist_songs song
LEFT JOIN public.artists a ON a.id=song.artist_id
ORDER BY song.id;

-- This two-FK relation has no occurrence ID, order, set boundary or timestamps.
-- Aggregate only identical native pairs; multiplicity preserves the entire multiset.
WITH grouped AS (
    SELECT played_setlist_song_id,played_setlist_show_id,count(*) AS multiplicity
    FROM public.setlist_songs_plays
    GROUP BY played_setlist_song_id,played_setlist_show_id
)
SELECT jsonb_build_object('kind','setlist_song_play',
    'played_setlist_song_id',p.played_setlist_song_id,'played_setlist_song_uuid',song.uuid,
    'played_setlist_show_id',p.played_setlist_show_id,'played_setlist_show_uuid',sh.uuid,
    'multiplicity',p.multiplicity)
FROM grouped p
LEFT JOIN public.setlist_songs song ON song.id=p.played_setlist_song_id
LEFT JOIN public.setlist_shows sh ON sh.id=p.played_setlist_show_id
ORDER BY p.played_setlist_song_id,p.played_setlist_show_id;

SELECT jsonb_build_object('kind','snapshot_complete','completed_at',clock_timestamp());
COMMIT;
