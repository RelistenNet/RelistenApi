-- IDs and association keys only. This does not assign any new resource a partition.
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL standard_conforming_strings = on;
SELECT jsonb_build_object('kind','snapshot','snapshot_utc',transaction_timestamp(),
    'read_only',current_setting('transaction_read_only'),'in_recovery',pg_is_in_recovery(),
    'snapshot',txid_current_snapshot()::text,'replay_lsn',pg_last_wal_replay_lsn(),
    'migration_versions',(SELECT jsonb_agg(version ORDER BY version) FROM versioninfo));

WITH allowed_venues AS (
    SELECT * FROM jsonb_to_recordset(__VENUE_ALLOWLIST__::jsonb)
    AS x(evidence_id text,venue_id bigint,venue_uuid uuid,artist_id integer,artist_uuid uuid)
), authenticated_venues AS (
    SELECT av.* FROM allowed_venues av
    JOIN venues v ON v.id=av.venue_id AND v.uuid=av.venue_uuid AND v.artist_id=av.artist_id
    JOIN artists a ON a.id=v.artist_id AND a.uuid=av.artist_uuid
), associations AS (
    SELECT av.evidence_id AS venue_evidence_id,av.venue_id,av.venue_uuid,av.artist_id,av.artist_uuid,
        'source_venue'::text AS association_kind,s.id AS source_id,s.uuid AS source_uuid,
        sh.id AS show_id,sh.uuid AS show_uuid
    FROM authenticated_venues av JOIN sources s ON s.venue_id=av.venue_id AND s.artist_id=av.artist_id
    LEFT JOIN shows sh ON sh.id=s.show_id AND sh.artist_id=av.artist_id
    UNION ALL
    SELECT av.evidence_id,av.venue_id,av.venue_uuid,av.artist_id,av.artist_uuid,
        'show_venue',NULL::bigint,NULL::uuid,sh.id,sh.uuid
    FROM authenticated_venues av JOIN shows sh ON sh.venue_id=av.venue_id AND sh.artist_id=av.artist_id
    UNION ALL
    SELECT av.evidence_id,av.venue_id,av.venue_uuid,av.artist_id,av.artist_uuid,
        'show_source',s.id,s.uuid,sh.id,sh.uuid
    FROM authenticated_venues av JOIN shows sh ON sh.venue_id=av.venue_id AND sh.artist_id=av.artist_id
    JOIN sources s ON s.show_id=sh.id AND s.artist_id=av.artist_id
), bounded AS MATERIALIZED (
    SELECT * FROM associations LIMIT 100001
), bounds_guard AS MATERIALIZED (
    SELECT 1 / CASE WHEN count(*)<=100000 THEN 1 ELSE 0 END AS valid FROM bounded
)
SELECT jsonb_build_object('kind','venue_association_metadata',
    'requested_venue_count',(SELECT count(*) FROM allowed_venues),
    'authenticated_venue_count',(SELECT count(*) FROM authenticated_venues),
    'associations',COALESCE((SELECT jsonb_agg(to_jsonb(b)
        ORDER BY b.venue_evidence_id,b.association_kind,b.show_id,b.source_id) FROM bounded b),'[]'::jsonb))
FROM bounds_guard WHERE valid=1;
SELECT jsonb_build_object('kind','snapshot_complete','completed_at',clock_timestamp());
COMMIT;
