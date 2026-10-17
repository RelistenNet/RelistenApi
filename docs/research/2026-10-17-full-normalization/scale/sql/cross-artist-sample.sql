-- Format profiles cover whole relations; sampling is deterministic within artist.
-- Raw text is evidence only. This query does not create normalization labels.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT jsonb_build_object('kind','snapshot','captured_at',now(),
 'read_only',current_setting('transaction_read_only'),'in_recovery',pg_is_in_recovery(),
 'snapshot',pg_current_snapshot()::text,'replay_lsn',pg_last_wal_replay_lsn(),
 'migration_versions',(select jsonb_agg(version order by version) from versioninfo));
SELECT jsonb_build_object('kind','schema','rows',jsonb_agg(x)) FROM (
 SELECT table_schema,table_name,column_name,data_type,is_nullable FROM information_schema.columns
 WHERE table_schema='public' AND table_name in ('artists','sources','source_tracks','source_sets',
 'shows','venues','setlist_songs','setlist_shows','setlist_songs_plays','artists_upstream_sources','upstream_sources')
 ORDER BY table_name,ordinal_position) x;
SELECT jsonb_build_object('kind','indexes','rows',jsonb_agg(x)) FROM (
 SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='public'
 AND tablename in ('sources','source_tracks','venues','setlist_songs','setlist_shows','setlist_songs_plays')
 ORDER BY tablename,indexname) x;
SELECT jsonb_build_object('kind','artist','row',to_jsonb(a),'configured_provider_mappings',
 coalesce((select jsonb_agg(jsonb_build_object('provider_id',u.id,'provider_name',u.name,
 'upstream_identifier',aus.upstream_identifier) order by u.id,aus.upstream_identifier)
 from artists_upstream_sources aus join upstream_sources u on u.id=aus.upstream_source_id where aus.artist_id=a.id),'[]'::jsonb))
 FROM artists a ORDER BY a.id;
SELECT jsonb_build_object('kind','source_profile','row',to_jsonb(x)) FROM (
 SELECT artist_id,count(*) as rows,
 count(*) filter(where nullif(btrim(taper),'') is not null) as taper_present,
 count(*) filter(where nullif(btrim(transferrer),'') is not null) as transferrer_present,
 count(*) filter(where nullif(btrim(lineage),'') is not null) as lineage_present,
 count(*) filter(where nullif(btrim(taper_notes),'') is not null) as notes_present,
 count(*) filter(where description ~ '<[^>]+>') as html_description,
 count(*) filter(where taper ~ '[;,&/]' or transferrer ~ '[;,&/]') as compound_credit_candidate,
 count(*) filter(where lineage ~ '>') as lineage_separator,
 count(*) filter(where length(coalesce(taper,''))>120 or length(coalesce(transferrer,''))>120) as long_credit,
 count(*) filter(where display_date ~* 'x') as partial_date,
 max(length(taper)) as max_taper_characters,max(length(transferrer)) as max_transferrer_characters,
 max(length(lineage)) as max_lineage_characters,max(length(description)) as max_description_characters
 FROM sources GROUP BY artist_id) x ORDER BY artist_id;
SELECT jsonb_build_object('kind','track_profile','row',to_jsonb(x)) FROM (
 SELECT artist_id,count(*) as rows,
 count(*) filter(where is_orphaned is false) as active,
 count(*) filter(where is_orphaned is true) as orphaned,
 count(*) filter(where is_orphaned is null) as unknown_orphan_state,
 count(*) filter(where is_orphaned is false and title ~ '>|→|»') as active_segue_candidate,
 count(*) filter(where is_orphaned is false and title ~ '[*/]') as active_annotation_or_compound_candidate,
 count(*) filter(where is_orphaned is false and title ~ '\([^)]*\)|\[[^]]*\]') as active_bracketed_text,
 count(*) filter(where is_orphaned is false and nullif(btrim(title),'') is null) as active_empty_title,
 max(length(title)) filter(where is_orphaned is false) as max_active_title_characters
 FROM source_tracks GROUP BY artist_id) x ORDER BY artist_id;
SELECT jsonb_build_object('kind','venue_profile','row',to_jsonb(x)) FROM (
 SELECT artist_id,count(*) as rows,count(*) filter(where latitude is not null and longitude is not null) as coordinates,
 count(*) filter(where nullif(btrim(past_names),'') is not null) as past_names,
 count(*) filter(where nullif(btrim(location),'') is not null) as location_present
 FROM venues GROUP BY artist_id) x ORDER BY artist_id nulls first;
SELECT jsonb_build_object('kind','song_profile','row',to_jsonb(x)) FROM (
 SELECT artist_id,count(*) as rows FROM setlist_songs GROUP BY artist_id) x ORDER BY artist_id;
SELECT jsonb_build_object('kind','setlist_profile','row',to_jsonb(x)) FROM (
 SELECT ss.artist_id,count(distinct ss.id) as shows,count(p.played_setlist_song_id) as memberships
 FROM setlist_shows ss left join setlist_songs_plays p on p.played_setlist_show_id=ss.id GROUP BY ss.artist_id) x ORDER BY artist_id;
-- v1 seed is fixed. Hash rank avoids selecting only earliest IDs.
WITH ranked AS (
 SELECT s.*,row_number() over(partition by artist_id order by md5('normalization-scale-v1:source:'||id::text),id) sample_rank
 FROM sources s)
SELECT jsonb_build_object('kind','source_sample','rank',s.sample_rank,'family','artist:'||s.artist_id||':show:'||s.show_id,
 'row',to_jsonb(s)-'sample_rank') FROM ranked s WHERE sample_rank<=8 ORDER BY artist_id,sample_rank;
WITH ranked AS (
 SELECT t.*,row_number() over(partition by artist_id order by md5('normalization-scale-v1:track:'||id::text),id) sample_rank
 FROM source_tracks t WHERE is_orphaned is false)
SELECT jsonb_build_object('kind','track_sample','rank',t.sample_rank,'row',to_jsonb(t)-'sample_rank',
 'source_context',to_jsonb(s),'set_context',to_jsonb(st),'show_context',to_jsonb(sh),
 'family','artist:'||t.artist_id||':show:'||s.show_id)
 FROM ranked t left join sources s on s.id=t.source_id left join source_sets st on st.id=t.source_set_id
 left join shows sh on sh.id=s.show_id WHERE sample_rank<=8 ORDER BY t.artist_id,sample_rank;
WITH ranked AS (
 SELECT v.*,row_number() over(partition by artist_id order by md5('normalization-scale-v1:venue:'||id::text),id) sample_rank
 FROM venues v)
SELECT jsonb_build_object('kind','venue_sample','rank',v.sample_rank,'row',to_jsonb(v)-'sample_rank')
 FROM ranked v WHERE sample_rank<=4 ORDER BY artist_id nulls first,sample_rank;
WITH ranked AS (
 SELECT s.*,row_number() over(partition by artist_id order by md5('normalization-scale-v1:song:'||id::text),id) sample_rank
 FROM setlist_songs s)
SELECT jsonb_build_object('kind','song_sample','rank',s.sample_rank,'row',to_jsonb(s)-'sample_rank')
 FROM ranked s WHERE sample_rank<=32 ORDER BY artist_id,sample_rank;
WITH ranked AS (
 SELECT s.*,row_number() over(partition by artist_id order by md5('normalization-scale-v1:setlist:'||id::text),id) sample_rank
 FROM setlist_shows s)
SELECT jsonb_build_object('kind','setlist_sample','rank',s.sample_rank,'row',to_jsonb(s)-'sample_rank',
 'unordered_memberships',coalesce((select jsonb_agg(jsonb_build_object('song_id',song.id,'song',to_jsonb(song)) order by song.id)
 FROM setlist_songs_plays p join setlist_songs song on song.id=p.played_setlist_song_id
 WHERE p.played_setlist_show_id=s.id),'[]'::jsonb)) FROM ranked s WHERE sample_rank<=8 ORDER BY artist_id,sample_rank;
SELECT jsonb_build_object('kind','totals','rows',jsonb_build_object('artists',(select count(*) from artists),
 'sources',(select count(*) from sources),'tracks',(select count(*) from source_tracks),'venues',(select count(*) from venues),
 'songs',(select count(*) from setlist_songs),'setlist_shows',(select count(*) from setlist_shows),
 'setlist_memberships',(select count(*) from setlist_songs_plays)));
SELECT jsonb_build_object('kind','snapshot_complete','completed_at',clock_timestamp());
COMMIT;
