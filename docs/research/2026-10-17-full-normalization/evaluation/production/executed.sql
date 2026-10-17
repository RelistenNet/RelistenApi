-- Curated development inputs. No canonical identities or performance order are inferred.
-- Run in one read-only snapshot; every prose field retained here is complete.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SELECT jsonb_build_object('kind','snapshot','snapshot_utc',transaction_timestamp(),
 'transaction_read_only',current_setting('transaction_read_only'),
 'is_replica',pg_is_in_recovery(),'postgres_version',version());
WITH cohort(id) AS (VALUES(4),(5),(9),(14),(17),(161),(166),(167),(175),(180),(202),(278),(286),(327),(2291),(4411)),
base_sources AS (
 SELECT s.* FROM cohort c CROSS JOIN LATERAL (
  SELECT * FROM sources WHERE artist_id=c.id
   AND greatest(length(coalesce(source,'')),length(coalesce(lineage,'')),length(coalesce(taper,'')),length(coalesce(transferrer,''))) <= 2000
   AND (coalesce(taper,'')||coalesce(transferrer,'')||coalesce(lineage,'')||coalesce(source,'')) !~ '@'
  ORDER BY id LIMIT 2
 ) s
),
credit_sources AS (
 SELECT s.* FROM cohort c CROSS JOIN LATERAL (
  SELECT * FROM sources WHERE artist_id=c.id
   AND (taper='Scott Bernstein' OR transferrer='Charlie Miller' OR taper='Keith Litzenberger')
   AND greatest(length(coalesce(source,'')),length(coalesce(lineage,'')),length(coalesce(taper,'')),length(coalesce(transferrer,''))) <= 2000
   AND (coalesce(taper,'')||coalesce(transferrer,'')||coalesce(lineage,'')||coalesce(source,'')) !~ '@'
  ORDER BY id LIMIT 1
 ) s
),
selected_sources AS (SELECT * FROM base_sources UNION SELECT * FROM credit_sources),
selected_tracks AS (
 SELECT t.* FROM cohort c CROSS JOIN LATERAL (
  SELECT t.* FROM source_tracks t JOIN selected_sources s ON s.id=t.source_id
  WHERE t.artist_id=c.id AND NOT t.is_orphaned
  ORDER BY CASE WHEN t.title ~* '(>|->| / |intro|banter|unknown|tuning|medley)' THEN 0 ELSE 1 END,t.id LIMIT 2
 ) t
),
selected_venues AS (
 SELECT * FROM venues WHERE id IN (56592,56542,56510,269714,118929,134026,97200,134887,135101,268020,100569,269119,94057,108148,98670,160232,161278,62565,55877,106502)
),
selected_songs AS (
 SELECT * FROM setlist_songs WHERE id IN (18459,18513,18512,2627,11442,34443,18587,2696,11427,34543,18536,18799,18525,2634,11376,33427)
),
selected_setlists AS (
 SELECT ss.* FROM (VALUES(4),(9),(14),(17)) c(id) CROSS JOIN LATERAL (
  SELECT * FROM setlist_shows WHERE artist_id=c.id ORDER BY id LIMIT 1
 ) ss
),
records AS (
 SELECT 'source:'||s.id id,'source' kind,s.artist_id,s.id entity_id,
 CASE WHEN s.artist_id=4 THEN 'phish.in' ELSE 'archive.org' END provider,
 s.upstream_identifier source_identifier,s.id source_id,s.show_id,
 jsonb_build_object('uuid',s.uuid,'display_date',s.display_date,'taper',s.taper,'transferrer',s.transferrer,'source',s.source,'lineage',s.lineage,'is_soundboard',s.is_soundboard,'is_remaster',s.is_remaster,'updated_at',s.updated_at) fields
 FROM selected_sources s
 UNION ALL
 SELECT 'track:'||t.id,'track',t.artist_id,t.id,
 CASE WHEN t.artist_id=4 THEN 'phish.in' ELSE 'archive.org' END,
 s.upstream_identifier,t.source_id,s.show_id,
 jsonb_build_object('title',t.title,'uuid',t.uuid,'source_set_id',t.source_set_id,'track_position',t.track_position,'duration',t.duration,'updated_at',t.updated_at)
 FROM selected_tracks t JOIN selected_sources s ON s.id=t.source_id
 UNION ALL
 SELECT 'venue:'||v.id,'venue',v.artist_id,v.id,
 CASE WHEN v.artist_id=4 THEN 'phish.in' WHEN v.upstream_identifier LIKE 'jerrygarcia.com_%' THEN 'jerrygarcia.com' WHEN v.upstream_identifier LIKE 'pt:%' THEN 'phantasytour.com' ELSE 'archive.org' END,
 v.upstream_identifier,NULL,NULL,
 jsonb_build_object('name',v.name,'location',v.location,'latitude',v.latitude,'longitude',v.longitude,'past_names',v.past_names,'uuid',v.uuid,'updated_at',v.updated_at)
 FROM selected_venues v
 UNION ALL
 SELECT 'song:'||s.id,'song',s.artist_id,s.id,'artist-provider-association',s.upstream_identifier,NULL,NULL,
 jsonb_build_object('name',s.name,'slug',s.slug,'uuid',s.uuid,'updated_at',s.updated_at)
 FROM selected_songs s
 UNION ALL
 SELECT 'setlist:'||ss.id,'setlist',ss.artist_id,ss.id,
 CASE ss.artist_id WHEN 4 THEN 'phish.net' WHEN 9 THEN 'jerrygarcia.com' WHEN 14 THEN 'setlist.fm' WHEN 17 THEN 'phantasytour.com' END,
 ss.upstream_identifier,NULL,NULL,
 jsonb_build_object('date',ss.date,'venue_id',ss.venue_id,'uuid',ss.uuid,'song_memberships',
 (SELECT coalesce(jsonb_agg(jsonb_build_object('song_id',p.played_setlist_song_id,'song_name',s.name) ORDER BY p.played_setlist_song_id),'[]'::jsonb) FROM setlist_songs_plays p JOIN setlist_songs s ON s.id=p.played_setlist_song_id WHERE p.played_setlist_show_id=ss.id),
 'order_available',false,'updated_at',ss.updated_at)
 FROM selected_setlists ss
)
SELECT jsonb_build_object('id',r.id,'kind',r.kind,'artist_id',r.artist_id,'artist_name',a.name,
 'provider',r.provider,'provider_basis','Importer identifier conventions; artist-provider-association means row lacks provider ownership',
 'entity_id',r.entity_id,'source_identifier',r.source_identifier,'source_id',r.source_id,'show_id',r.show_id,
 'artist_provider_associations',(SELECT jsonb_agg(jsonb_build_object('provider',u.name,'identifier',aus.upstream_identifier) ORDER BY u.id) FROM artists_upstream_sources aus JOIN upstream_sources u ON u.id=aus.upstream_source_id WHERE aus.artist_id=r.artist_id),
 'fields',r.fields)
FROM records r JOIN artists a ON a.id=r.artist_id ORDER BY r.kind,r.artist_id,r.entity_id;
COMMIT;
