-- Expanded development inputs retain a discovery show UUID, not an original show UUID.
-- Date context requires that current discovery identity as well as the original numeric assignment.
-- This query uses production schema12. Catalog sort dates are not event-date precision.
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL standard_conforming_strings = on;
SET LOCAL TIME ZONE 'UTC';
SELECT jsonb_build_object('kind','snapshot','snapshot_utc',transaction_timestamp(),
    'read_only',current_setting('transaction_read_only'),'in_recovery',pg_is_in_recovery(),
    'snapshot',txid_current_snapshot()::text,'replay_lsn',pg_last_wal_replay_lsn(),
    'migration_versions',(SELECT jsonb_agg(version ORDER BY version) FROM versioninfo));

WITH allowed_venues AS (
    SELECT * FROM jsonb_to_recordset(__VENUE_ALLOWLIST__::jsonb)
    AS x(evidence_id text,venue_id bigint,venue_uuid uuid,artist_id integer,
        artist_uuid uuid,expected_metadata jsonb)
), allowed_sources AS (
    SELECT * FROM jsonb_to_recordset(__SOURCE_ALLOWLIST__::jsonb)
    AS x(evidence_id text,source_id bigint,source_uuid uuid,artist_id integer,
        artist_uuid uuid,expected_show_id bigint,family_group text,current_discovery_show_uuid uuid)
), venue_observations AS MATERIALIZED (
    SELECT av.*,v.uuid AS current_venue_uuid,v.artist_id AS current_artist_id,
        a.uuid AS current_artist_uuid,
        (v.uuid=av.venue_uuid AND v.artist_id=av.artist_id AND a.uuid=av.artist_uuid)
            IS TRUE AS identity_authenticated,
        CASE WHEN v.uuid=av.venue_uuid AND v.artist_id=av.artist_id AND a.uuid=av.artist_uuid
            THEN jsonb_build_object('name',v.name,'location',v.location,'past_names',v.past_names,
                'latitude',v.latitude,'longitude',v.longitude,
                'upstream_identifier',v.upstream_identifier,'slug',v.slug)
        END AS current_metadata,
        CASE WHEN v.uuid=av.venue_uuid AND v.artist_id=av.artist_id AND a.uuid=av.artist_uuid
            THEN jsonb_build_object('created_at',v.created_at,'updated_at',v.updated_at)
        END AS catalog_row_times
    FROM allowed_venues av LEFT JOIN venues v ON v.id=av.venue_id
    LEFT JOIN artists a ON a.id=v.artist_id
), venue_receipts AS MATERIALIZED (
    SELECT v.*,(v.identity_authenticated AND v.current_metadata=v.expected_metadata)
        AS baseline_metadata_unchanged,
        CASE WHEN current_venue_uuid IS NULL THEN 'missing_or_rekeyed'
            WHEN current_venue_uuid<>venue_uuid THEN 'uuid_changed'
            WHEN NOT identity_authenticated THEN 'artist_changed'
            WHEN current_metadata<>expected_metadata THEN 'metadata_changed'
            ELSE 'unchanged' END AS observation_status
    FROM venue_observations v
), source_observations AS MATERIALIZED (
    SELECT asp.*,s.uuid AS current_source_uuid,s.artist_id AS current_artist_id,
        a.uuid AS current_artist_uuid,s.show_id AS current_show_id,
        (s.uuid=asp.source_uuid) IS TRUE AS source_uuid_authenticated,
        (s.artist_id=asp.artist_id AND a.uuid=asp.artist_uuid) IS TRUE AS source_artist_authenticated,
        (s.show_id=asp.expected_show_id) IS TRUE AS show_assignment_unchanged,
        sh.uuid AS current_show_uuid,
        (sh.uuid=asp.current_discovery_show_uuid) IS TRUE AS discovery_show_uuid_authenticated,
        (sh.artist_id=asp.artist_id AND show_artist.uuid=asp.artist_uuid)
            IS TRUE AS show_artist_authenticated,
        CASE WHEN s.uuid=asp.source_uuid AND s.artist_id=asp.artist_id AND a.uuid=asp.artist_uuid
            THEN s.venue_id END AS current_source_venue_id,
        CASE WHEN sh.artist_id=asp.artist_id AND show_artist.uuid=asp.artist_uuid
            THEN sh.venue_id END AS current_show_venue_id,
        CASE WHEN s.uuid=asp.source_uuid AND s.artist_id=asp.artist_id AND a.uuid=asp.artist_uuid
            AND s.show_id=asp.expected_show_id AND sh.artist_id=asp.artist_id
            AND show_artist.uuid=asp.artist_uuid AND sh.uuid=asp.current_discovery_show_uuid
            THEN s.display_date END AS source_display_date,
        CASE WHEN s.uuid=asp.source_uuid AND s.artist_id=asp.artist_id AND a.uuid=asp.artist_uuid
            AND s.show_id=asp.expected_show_id AND sh.artist_id=asp.artist_id
            AND show_artist.uuid=asp.artist_uuid AND sh.uuid=asp.current_discovery_show_uuid
            THEN sh.display_date END AS show_display_date,
        CASE WHEN s.uuid=asp.source_uuid AND s.artist_id=asp.artist_id AND a.uuid=asp.artist_uuid
            AND s.show_id=asp.expected_show_id AND sh.artist_id=asp.artist_id
            AND show_artist.uuid=asp.artist_uuid AND sh.uuid=asp.current_discovery_show_uuid
            THEN sh.date END AS show_catalog_sort_date,
        sv.evidence_id AS source_venue_evidence_id,hv.evidence_id AS show_venue_evidence_id,
        sv.baseline_metadata_unchanged AS source_venue_metadata_unchanged,
        hv.baseline_metadata_unchanged AS show_venue_metadata_unchanged
    FROM allowed_sources asp LEFT JOIN sources s ON s.id=asp.source_id
    LEFT JOIN artists a ON a.id=s.artist_id
    LEFT JOIN shows sh ON sh.id=asp.expected_show_id AND s.uuid=asp.source_uuid
        AND s.artist_id=asp.artist_id AND a.uuid=asp.artist_uuid
        AND s.show_id=asp.expected_show_id
    LEFT JOIN artists show_artist ON show_artist.id=sh.artist_id
    LEFT JOIN venue_receipts sv ON sv.venue_id=s.venue_id AND sv.identity_authenticated
        AND sv.artist_uuid=asp.artist_uuid AND sh.artist_id=asp.artist_id
        AND show_artist.uuid=asp.artist_uuid
    LEFT JOIN venue_receipts hv ON hv.venue_id=sh.venue_id AND hv.identity_authenticated
        AND hv.artist_uuid=asp.artist_uuid AND sh.artist_id=asp.artist_id
        AND show_artist.uuid=asp.artist_uuid
), contexts AS MATERIALIZED (
    SELECT evidence_id,source_id,source_uuid,artist_id,artist_uuid,expected_show_id,family_group,
        current_discovery_show_uuid,
        current_source_uuid,current_artist_id,current_artist_uuid,current_show_id,current_show_uuid,
        current_source_venue_id,current_show_venue_id,
        source_uuid_authenticated,source_artist_authenticated,show_assignment_unchanged,
        show_artist_authenticated,discovery_show_uuid_authenticated,
        source_venue_evidence_id,show_venue_evidence_id,
        source_venue_metadata_unchanged,show_venue_metadata_unchanged,
        CASE WHEN source_venue_evidence_id IS NOT NULL OR show_venue_evidence_id IS NOT NULL
            THEN source_display_date END AS source_display_date,
        CASE WHEN source_venue_evidence_id IS NOT NULL OR show_venue_evidence_id IS NOT NULL
            THEN show_display_date END AS show_display_date,
        CASE WHEN source_venue_evidence_id IS NOT NULL OR show_venue_evidence_id IS NOT NULL
            THEN show_catalog_sort_date END AS show_catalog_sort_date,
        CASE WHEN current_source_uuid IS NULL THEN 'source_missing'
            WHEN NOT source_uuid_authenticated THEN 'source_uuid_changed'
            WHEN NOT source_artist_authenticated THEN 'source_artist_changed'
            WHEN NOT show_assignment_unchanged THEN 'source_show_changed'
            WHEN current_show_uuid IS NULL THEN 'show_missing'
            WHEN NOT show_artist_authenticated THEN 'show_artist_changed'
            WHEN NOT discovery_show_uuid_authenticated THEN 'discovery_show_uuid_changed'
            WHEN source_venue_evidence_id IS NULL AND show_venue_evidence_id IS NULL
                THEN 'no_authenticated_selected_venue'
            ELSE 'authenticated' END AS observation_status
    FROM source_observations
), bounds_guard AS MATERIALIZED (
    -- Explicit failure rather than omitted rows or shortened strings. The CASE
    -- depends on query data, so an overflow prevents the completion marker.
    SELECT 1 / CASE WHEN
        (SELECT count(*) FROM venue_receipts)<=67 AND (SELECT count(*) FROM contexts)<=51
        AND COALESCE((SELECT max(octet_length(current_metadata::text)) FROM venue_receipts),0)<=65536
        AND COALESCE((SELECT max(COALESCE(octet_length(source_display_date),0)
            +COALESCE(octet_length(show_display_date),0))
            FROM contexts),0)<=8192
        THEN 1 ELSE 0 END AS valid
)
SELECT jsonb_build_object('kind','venue_date_context_supplement',
    'requested_venue_count',(SELECT count(*) FROM allowed_venues),
    'requested_source_count',(SELECT count(*) FROM allowed_sources),
    'venue_rows',COALESCE((SELECT jsonb_agg(to_jsonb(v) ORDER BY v.evidence_id)
        FROM venue_receipts v),'[]'::jsonb),
    'source_contexts',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.evidence_id)
        FROM contexts c),'[]'::jsonb))
FROM bounds_guard WHERE valid=1;
SELECT jsonb_build_object('kind','snapshot_complete','completed_at',clock_timestamp());
COMMIT;
