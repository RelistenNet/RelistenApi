BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SELECT jsonb_build_object('kind','snapshot','snapshot_utc',transaction_timestamp(),
 'transaction_read_only',current_setting('transaction_read_only'),
 'is_replica',pg_is_in_recovery(),'postgres_version',version());
SELECT jsonb_build_object('id','venue:'||v.id,'kind','venue','artist_id',v.artist_id,'artist_name',a.name,
 'provider','archive.org','provider_basis','Importer identifier convention; original identifier retained',
 'entity_id',v.id,'source_identifier',v.upstream_identifier,
 'fields',jsonb_build_object('name',v.name,'location',v.location,'latitude',v.latitude,'longitude',v.longitude,'past_names',v.past_names,'uuid',v.uuid,'updated_at',v.updated_at))
FROM venues v JOIN artists a ON a.id=v.artist_id
WHERE v.id IN (98538,98565,160562,98132,160536,34790,104547,28024)
ORDER BY v.id;
COMMIT;
