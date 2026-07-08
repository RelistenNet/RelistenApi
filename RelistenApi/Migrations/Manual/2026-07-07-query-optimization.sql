\set ON_ERROR_STOP on
\pset pager off

\echo 'Relisten query optimization maintenance'
\echo 'Safe default: this file only runs read-only preflight checks unless an apply_* variable is set to 1.'
\echo 'Run only through a direct connection to the PostgreSQL primary, never through PgBouncer.'

-- Read-only preflight. This section always runs.
SELECT
    current_database() AS database,
    current_user AS database_user,
    pg_is_in_recovery() AS is_replica,
    current_setting('server_version') AS postgres_version,
    (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb') AS timescaledb_version;

-- Refuse every mutating phase on a standby while still allowing the safe,
-- read-only default invocation to be rehearsed there.
\set mutation_requested false
\if :{?apply_relational_indexes}
  \if :apply_relational_indexes
    \set mutation_requested true
  \endif
\endif
\if :{?apply_orphan_cleanup}
  \if :apply_orphan_cleanup
    \set mutation_requested true
  \endif
\endif
\if :{?retire_old_show_info_indexes}
  \if :retire_old_show_info_indexes
    \set mutation_requested true
  \endif
\endif
\if :{?vacuum_full_show_info}
  \if :vacuum_full_show_info
    \set mutation_requested true
  \endif
\endif
\if :{?drop_timescale_id_idx}
  \if :drop_timescale_id_idx
    \set mutation_requested true
  \endif
\endif

SELECT NOT pg_is_in_recovery() AS server_is_primary \gset
\if :mutation_requested
  \if :server_is_primary
    \echo 'Primary-server guard passed.'
  \else
    \echo 'ERROR: refusing mutating maintenance on a replica.'
    -- Deliberately fail the psql process; ON_ERROR_STOP turns this into a
    -- non-zero exit without changing database state.
    SELECT current_setting('relisten.maintenance_requires_primary');
  \endif
\endif

SELECT
    to_regclass('public.source_tracks_source_id_idx') AS source_tracks_source_id_idx,
    to_regclass('public.idx_show_source_information_artist_source_count') AS show_info_covering_idx,
    to_regclass('public.idx_show_source_information_max_created_at') AS show_info_created_idx,
    to_regclass('public.idx_show_source_information_max_updated_at') AS show_info_updated_idx;

SELECT
    c.relname AS index_name,
    i.indisready,
    i.indisvalid,
    pg_size_pretty(pg_relation_size(c.oid)) AS size
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname IN (
    'idx_show_source_information_artist_id',
    'show_source_information_artist_id_idx',
    'idx_show_source_information_artist_source_count',
    'source_tracks_source_id_idx',
    'idx_show_source_information_max_created_at',
    'idx_show_source_information_max_updated_at'
)
ORDER BY c.relname;

SELECT count(*) AS orphan_source_review_counts
FROM source_review_counts counts
WHERE NOT EXISTS (
    SELECT 1
    FROM sources source
    WHERE source.id = counts.source_id
);

SELECT
    hypertable_name,
    num_chunks
FROM timescaledb_information.hypertables
WHERE hypertable_name = 'source_track_plays';

SELECT
    count(*) AS chunk_indexes,
    sum(s.idx_scan) AS scans,
    pg_size_pretty(sum(pg_relation_size(s.indexrelid))) AS total_size
FROM pg_stat_all_indexes s
WHERE s.schemaname = '_timescaledb_internal'
  AND s.indexrelname LIKE '%source_track_plays_new_id_idx';

-- Phase 1: ordinary-table indexes. Invoke with:
--   psql ... -v apply_relational_indexes=1 -f 2026-07-07-query-optimization.sql
\if :{?apply_relational_indexes}
  \if :apply_relational_indexes
    \echo 'Applying relational indexes concurrently'

    CREATE INDEX CONCURRENTLY source_tracks_source_id_idx
        ON public.source_tracks (source_id);

    CREATE INDEX CONCURRENTLY idx_show_source_information_artist_source_count
        ON public.show_source_information (artist_id)
        INCLUDE (source_count);

    CREATE INDEX CONCURRENTLY idx_show_source_information_max_created_at
        ON public.show_source_information (max_created_at DESC, show_id);

    CREATE INDEX CONCURRENTLY idx_show_source_information_max_updated_at
        ON public.show_source_information (max_updated_at DESC, show_id);

    ANALYZE public.source_tracks;
    ANALYZE public.show_source_information;
  \endif
\endif

-- Phase 2: one-time cleanup after the application has explicit deletes for both
-- source-deletion paths. The preflight count is currently expected to be zero.
-- Invoke with -v apply_orphan_cleanup=1.
\if :{?apply_orphan_cleanup}
  \if :apply_orphan_cleanup
    \echo 'Deleting existing orphan source_review_counts rows once'
    DELETE FROM public.source_review_counts counts
    WHERE NOT EXISTS (
        SELECT 1
        FROM public.sources source
        WHERE source.id = counts.source_id
    );
  \endif
\endif

-- Phase 3: retire the invalid and bloated predecessor indexes only after the
-- replacement covering index reports indisready=true and indisvalid=true.
-- Invoke with -v retire_old_show_info_indexes=1.
\if :{?retire_old_show_info_indexes}
  \if :retire_old_show_info_indexes
    \echo 'Dropping replaced show_source_information indexes concurrently'
    DROP INDEX CONCURRENTLY IF EXISTS public.idx_show_source_information_artist_id;
    DROP INDEX CONCURRENTLY IF EXISTS public.show_source_information_artist_id_idx;
  \endif
\endif

-- Phase 4: compact the 2.6 GiB show_source_information heap. pg_repack is not
-- installed in the production image, so this requires a maintenance window.
-- VACUUM FULL takes an ACCESS EXCLUSIVE lock and can temporarily create several
-- GiB of WAL and replica lag. Invoke only with -v vacuum_full_show_info=1.
\if :{?vacuum_full_show_info}
  \if :vacuum_full_show_info
    \echo 'Running blocking VACUUM FULL on show_source_information'
    VACUUM (FULL, ANALYZE, VERBOSE) public.show_source_information;
  \endif
\endif

-- Phase 5: remove the redundant Timescale parent (id) index. TimescaleDB 2.19
-- does not offer PostgreSQL DROP INDEX CONCURRENTLY across a hypertable's 2,812
-- chunks, so use a short lock timeout and a controlled window. The required
-- unique (id, created_at) index remains. Invoke with -v drop_timescale_id_idx=1.
\if :{?drop_timescale_id_idx}
  \if :drop_timescale_id_idx
    \echo 'Dropping redundant source_track_plays (id) hypertable index'
    SET lock_timeout = '5s';
    DROP INDEX public.source_track_plays_new_id_idx;
    RESET lock_timeout;
  \endif
\endif

-- Final read-only verification. This section always runs.
SELECT
    c.relname AS index_name,
    i.indisready,
    i.indisvalid,
    pg_size_pretty(pg_relation_size(c.oid)) AS size
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname IN (
    'idx_show_source_information_artist_source_count',
    'source_tracks_source_id_idx',
    'idx_show_source_information_max_created_at',
    'idx_show_source_information_max_updated_at'
)
ORDER BY c.relname;

SELECT
    pg_size_pretty(pg_relation_size('public.show_source_information')) AS show_info_heap,
    pg_size_pretty(pg_indexes_size('public.show_source_information')) AS show_info_indexes,
    pg_size_pretty(total_bytes) AS track_plays_total,
    pg_size_pretty(index_bytes) AS track_plays_indexes
FROM hypertable_detailed_size('public.source_track_plays');

SELECT count(*) AS orphan_source_review_counts_after
FROM source_review_counts counts
WHERE NOT EXISTS (
    SELECT 1
    FROM sources source
    WHERE source.id = counts.source_id
);

SELECT
    count(*) AS timescale_id_chunk_indexes_after,
    sum(s.idx_scan) AS scans,
    pg_size_pretty(sum(pg_relation_size(s.indexrelid))) AS total_size
FROM pg_stat_all_indexes s
WHERE s.schemaname = '_timescaledb_internal'
  AND s.indexrelname LIKE '%source_track_plays_new_id_idx';
