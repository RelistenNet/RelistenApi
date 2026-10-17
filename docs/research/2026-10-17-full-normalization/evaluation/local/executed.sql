-- Real upstream recordings imported into the local acceptance database.
-- Collection membership supplies context only; descriptions supply credit assertions.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SELECT jsonb_build_object('kind','snapshot','snapshot_utc',transaction_timestamp(),
 'transaction_read_only',current_setting('transaction_read_only'),
 'is_replica',pg_is_in_recovery(),'postgres_version',version());
WITH chosen AS (
 SELECT s.*,a.name artist_name FROM sources s JOIN artists a ON a.id=s.artist_id
 WHERE s.id IN(4283434,4283444,4283511,4283452,4284003,4284037,4283991,4283995)
), records AS (
 SELECT 'local:source:'||s.id id,'source' kind,s.id entity_id,s.artist_id,s.artist_name,s.upstream_identifier source_identifier,
 jsonb_build_object('uuid',s.uuid,'display_date',s.display_date,'taper',s.taper,'transferrer',s.transferrer,
 'source',s.source,'lineage',s.lineage,'description',s.description,'updated_at',s.updated_at,
 'collections',(SELECT jsonb_agg(jsonb_build_object('slug',c.slug,'uuid',c.uuid)) FROM collection_playable_sources cp JOIN collections c ON c.uuid=cp.collection_uuid WHERE cp.source_uuid=s.uuid),
 'observation',(SELECT jsonb_build_object('observation_id',o.observation_id,'content_hash',o.content_hash,'observed_at',o.observed_at,'provider_url',o.provider_url) FROM normalization.archive_item_observations o WHERE o.item_identifier=s.upstream_identifier ORDER BY observed_at DESC LIMIT 1)) fields
 FROM chosen s
 UNION ALL
 SELECT 'local:track:'||t.id,'track',t.id,s.artist_id,s.artist_name,s.upstream_identifier,
 jsonb_build_object('title',t.title,'uuid',t.uuid,'source_id',t.source_id,'source_set_id',t.source_set_id,'track_position',t.track_position,'duration',t.duration,'updated_at',t.updated_at)
 FROM chosen s CROSS JOIN LATERAL (
 SELECT * FROM source_tracks WHERE source_id=s.id AND NOT is_orphaned
 ORDER BY CASE WHEN title ~* '(>|->| / |intro|banter|unknown|tuning|medley)' THEN 0 ELSE 1 END,id LIMIT 1
 ) t
)
SELECT jsonb_build_object('id',id,'kind',kind,'entity_id',entity_id,'artist_id',artist_id,'artist_name',artist_name,
 'provider','archive.org','origin_database','relisten_jerry_20261017','source_identifier',source_identifier,
 'source_url','https://archive.org/details/'||source_identifier,'fields',fields)
FROM records ORDER BY kind,entity_id;
COMMIT;
