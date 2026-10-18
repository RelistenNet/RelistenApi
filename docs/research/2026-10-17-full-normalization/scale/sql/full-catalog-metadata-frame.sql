-- Fixed full metadata profile. The capture wrapper renders only authenticated seed coordinates.
-- No raw name, prose, contact, identifier or URL value leaves the server.
-- MD5 values are metadata fingerprints, NOT proof of entity/audio identity or
-- historical Python input_duplicate_family hashes. Do not use them as new labels.
-- Completion requires server_encoding=UTF8 and client_encoding=UTF8, so byte counts
-- and text hashes have the declared UTF-8 meaning. Other encodings are unsupported.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL TIME ZONE 'UTC';
SELECT jsonb_build_object(
    'kind','metadata_snapshot','version','full-catalog-metadata-frame-v1',
    'captured_at',transaction_timestamp(),'snapshot',pg_current_snapshot()::text,
    'read_only',current_setting('transaction_read_only'),
    'isolation',current_setting('transaction_isolation'),
    'server_version',current_setting('server_version'),
    'server_encoding',current_setting('server_encoding'),
    'client_encoding',current_setting('client_encoding'),
    'in_recovery',pg_is_in_recovery(),'replay_lsn',pg_last_wal_replay_lsn(),
    'last_replayed_transaction_at',pg_last_xact_replay_timestamp(),
    'provider_attribution','unavailable_on_sources',
    'date_precision','unavailable','knowledge_clock','unavailable',
    'text_profile_algorithm','md5_utf8_not_identity_proof',
    'content_derived_metadata_computed',true,
    'server_source_processing','nullness_length_md5_only','semantic_parsing',false);
SELECT jsonb_build_object('kind','metadata_counts',
    'artists',(SELECT count(*) FROM artists),
    'sources',(SELECT count(*) FROM sources),
    'shows',(SELECT count(*) FROM shows));
SELECT jsonb_build_object('kind','metadata_columns','table_name',table_name,
    'column_name',column_name,'data_type',data_type,'is_nullable',is_nullable)
FROM information_schema.columns
WHERE table_schema='public' AND table_name IN
    ('artists','sources','shows')
ORDER BY table_name,ordinal_position;

-- BEGIN ARTISTS
-- The artist roster starts from artists, including artists with no recordings.
WITH source_counts AS (SELECT artist_id,count(*) AS sources FROM sources GROUP BY artist_id)
SELECT jsonb_build_object('kind','artist','id',a.id,'uuid',a.uuid,
    'created_at',a.created_at,'updated_at',a.updated_at,'featured',a.featured,
    'source_count',coalesce(c.sources,0),'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
                'is_null',p.value IS NULL,'characters',char_length(p.value),
                'utf8_bytes',octet_length(p.value),
                'md5_utf8',md5(p.value)))
            FROM (VALUES ('name',a.name),
                ('slug',a.slug),
                ('sort_name',a.sort_name),
                ('musicbrainz_id',a.musicbrainz_id)) AS p(field,value)))
FROM artists a LEFT JOIN source_counts c ON c.artist_id=a.id
ORDER BY a.id,a.uuid;

-- END ARTISTS

-- BEGIN SOURCES
-- Original pointers are retained independently of whether their joins resolve.
SELECT jsonb_build_object('kind','source','id',s.id,'uuid',s.uuid,
    'artist_id',s.artist_id,'artist_uuid',a.uuid,
    'show_id',s.show_id,'show_uuid',sh.uuid,'show_artist_id',sh.artist_id,
    'venue_id',s.venue_id,'show_venue_id',sh.venue_id,
    'created_at',s.created_at,'updated_at',s.updated_at,
    'is_soundboard',s.is_soundboard,'is_remaster',s.is_remaster,'has_jamcharts',s.has_jamcharts,
    'avg_rating',s.avg_rating,'num_reviews',s.num_reviews,
    'avg_rating_weighted',s.avg_rating_weighted,'duration',s.duration,'num_ratings',s.num_ratings,
    'stored_flac_type',s.flac_type,'provider',NULL,'verified_audio_format',NULL,
    'knowledge_at',NULL,'input_revision_uuid',NULL,'input_generation',NULL,
    'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
                'is_null',p.value IS NULL,'characters',char_length(p.value),
                'utf8_bytes',octet_length(p.value),
                'md5_utf8',md5(p.value)))
            FROM (VALUES ('upstream_identifier',s.upstream_identifier),
                ('display_date',s.display_date),
                ('description',s.description),
                ('taper_notes',s.taper_notes),
                ('source',s.source),
                ('taper',s.taper),
                ('transferrer',s.transferrer),
                ('lineage',s.lineage)) AS p(field,value)))
FROM sources s LEFT JOIN artists a ON a.id=s.artist_id LEFT JOIN shows sh ON sh.id=s.show_id
ORDER BY s.id,s.uuid;

-- END SOURCES

-- BEGIN SHOWS
SELECT jsonb_build_object('kind','show','id',sh.id,'uuid',sh.uuid,
    'artist_id',sh.artist_id,'artist_uuid',a.uuid,'venue_id',sh.venue_id,
    'tour_id',sh.tour_id,'year_id',sh.year_id,'era_id',sh.era_id,
    'catalog_sort_date',sh.date,'event_date_precision',NULL,
    'avg_rating',sh.avg_rating,'avg_duration',sh.avg_duration,
    'created_at',sh.created_at,'updated_at',sh.updated_at,
    'text_profiles',(SELECT jsonb_object_agg(p.field,jsonb_build_object(
                'is_null',p.value IS NULL,'characters',char_length(p.value),
                'utf8_bytes',octet_length(p.value),
                'md5_utf8',md5(p.value)))
            FROM (VALUES ('display_date',sh.display_date)) AS p(field,value)))
FROM shows sh LEFT JOIN artists a ON a.id=sh.artist_id
ORDER BY sh.id,sh.uuid;

-- END SHOWS

-- BEGIN LOOKUPS
-- The wrapper authenticates and validates the exact metadata-only reservation seed
-- artifact, then supplies every requested coordinate. This executable template uses a
-- required psql quoted JSON literal; missing variables fail rather than imply no seeds.
-- The maintained wrapper will render bounded chunks of this fixed SELECT inside the
-- SAME transaction, avoiding command-line size limits for larger exposure inventories.
WITH requested AS (
    SELECT ref->>'catalog_namespace' AS catalog_namespace,
        (ref->>'track_id')::bigint AS track_id,(ref->>'track_uuid')::uuid AS track_uuid
    FROM jsonb_array_elements(:'track_lookup_seeds_json'::jsonb) AS x(ref)
), coordinate_lookups AS (
    SELECT r.*, 'native_id' AS coordinate,t.id,t.uuid,t.artist_id,t.source_id,t.source_set_id
    FROM requested r LEFT JOIN source_tracks t
        ON r.catalog_namespace='catalog-2026-10-17' AND t.id=r.track_id
    WHERE r.track_id IS NOT NULL
    UNION ALL
    SELECT r.*, 'uuid' AS coordinate,t.id,t.uuid,t.artist_id,t.source_id,t.source_set_id
    FROM requested r LEFT JOIN source_tracks t
        ON r.catalog_namespace='catalog-2026-10-17' AND t.uuid=r.track_uuid
    WHERE r.track_uuid IS NOT NULL
)
SELECT jsonb_build_object('kind','track_parent_lookup',
    'requested',jsonb_build_object('catalog_namespace',r.catalog_namespace,
        'track_id',r.track_id,'track_uuid',r.track_uuid),'coordinate',r.coordinate,
    'lookup_status',CASE WHEN r.catalog_namespace<>'catalog-2026-10-17' THEN 'different_catalog_not_queried'
        WHEN r.id IS NULL THEN 'missing_current_row' ELSE 'found_current_row' END,
    'id',r.id,'uuid',r.uuid,'artist_id',r.artist_id,
    'source_id',r.source_id,'source_uuid',direct_source.uuid,
    'source_artist_id',direct_source.artist_id,'source_show_id',direct_source.show_id,
    'source_show_uuid',direct_show.uuid,
    'source_set_id',r.source_set_id,'source_set_uuid',st.uuid,
    'set_source_id',st.source_id,'set_source_uuid',set_source.uuid,
    'set_source_artist_id',set_source.artist_id,'set_source_show_id',set_source.show_id,
    'set_source_show_uuid',set_show.uuid,
    'historical_binding_verified',NULL)
FROM coordinate_lookups r
LEFT JOIN sources direct_source ON direct_source.id=r.source_id
LEFT JOIN shows direct_show ON direct_show.id=direct_source.show_id
LEFT JOIN source_sets st ON st.id=r.source_set_id
LEFT JOIN sources set_source ON set_source.id=st.source_id
LEFT JOIN shows set_show ON set_show.id=set_source.show_id
ORDER BY r.catalog_namespace,r.track_id NULLS LAST,r.track_uuid NULLS LAST,r.coordinate,r.id,r.uuid;
-- END LOOKUPS
SELECT jsonb_build_object('kind','snapshot_complete','completed_at',clock_timestamp());
COMMIT;
