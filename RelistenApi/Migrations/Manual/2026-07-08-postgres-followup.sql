\set ON_ERROR_STOP on
\pset pager off

\echo 'Relisten PostgreSQL follow-up maintenance'
\echo 'Safe default: only read-only preflight and verification queries run unless an apply/drop variable is set to 1.'
\echo 'Mutating phases must run through a direct connection to the PostgreSQL primary, never through PgBouncer.'

SELECT
    current_database() AS database,
    current_user AS database_user,
    pg_is_in_recovery() AS is_replica,
    current_setting('server_version') AS postgres_version,
    (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb') AS timescaledb_version;

\set mutation_requested false
\if :{?apply_relational_indexes}
  \if :apply_relational_indexes
    \set mutation_requested true
  \endif
\endif
\if :{?retire_duplicate_show_index}
  \if :retire_duplicate_show_index
    \set mutation_requested true
  \endif
\endif
\if :{?drop_duplicate_cagg_indexes}
  \if :drop_duplicate_cagg_indexes
    \set mutation_requested true
  \endif
\endif
\if :{?drop_legacy_track_play_storage}
  \if :drop_legacy_track_play_storage
    \set mutation_requested true
  \endif
\endif

SELECT NOT pg_is_in_recovery() AS server_is_primary \gset
\if :mutation_requested
  \if :server_is_primary
    \echo 'Primary-server guard passed.'
  \else
    \echo 'ERROR: refusing mutating maintenance on a replica.'
    SELECT current_setting('relisten.maintenance_requires_primary');
  \endif
\endif

SELECT
    c.relname AS index_name,
    i.indisready,
    i.indisvalid,
    pg_size_pretty(pg_relation_size(c.oid)) AS size,
    pg_get_indexdef(c.oid) AS definition
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname IN (
    'idx_shows_artist_id_venue_id',
    'idx_shows_month_day',
    'idx_shows_artist_id_display_date',
    'shows_artist_id_display_date_key'
)
ORDER BY c.relname;

SELECT
    c.oid::regclass AS object,
    c.relkind,
    pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size
FROM pg_class c
WHERE c.oid IN (
    to_regclass('public.source_track_plays_old'),
    to_regclass('public.source_track_plays_by_hour_48h'),
    to_regclass('public.source_track_plays_by_day_6mo'),
    to_regclass('public.source_track_plays_enriched')
)
ORDER BY c.oid::regclass::text;

SELECT to_regclass('public.source_track_plays_old') IS NOT NULL AS legacy_table_exists \gset
\if :legacy_table_exists
  SELECT
      approximate_row_count('public.source_track_plays'::regclass) AS current_approximate_rows,
      approximate_row_count('public.source_track_plays_old'::regclass) AS legacy_approximate_rows,
      (SELECT min(created_at) FROM public.source_track_plays_old) AS legacy_min_created_at,
      (SELECT max(created_at) FROM public.source_track_plays_old) AS legacy_max_created_at;
\else
  \echo 'Legacy source_track_plays storage has already been removed.'
\endif

-- Read-only exact coverage proof. This can run on the replica with
-- -v verify_legacy_coverage=1. It compares every legacy row, including all
-- payload columns, against the corresponding one-day Timescale chunk. Direct
-- chunk access avoids replanning a 2,800-chunk hypertable for every day.
\if :{?verify_legacy_coverage}
  \if :verify_legacy_coverage
    \echo 'Verifying every source_track_plays_old row exists unchanged in source_track_plays'
    DO $verify_legacy_coverage$
    DECLARE
        chunk record;
        chunk_rows bigint;
        mismatched_rows bigint;
        checked_rows bigint := 0;
        checked_chunks integer := 0;
        previous_end timestamptz;
        legacy_min timestamptz;
        legacy_max timestamptz;
        has_uncovered_rows boolean;
    BEGIN
        SELECT min(created_at), max(created_at)
        INTO legacy_min, legacy_max
        FROM public.source_track_plays_old;

        IF legacy_min IS NULL OR legacy_max IS NULL THEN
            RAISE EXCEPTION 'source_track_plays_old is empty; inspect before dropping it';
        END IF;

        FOR chunk IN
            SELECT chunk_schema, chunk_name, range_start, range_end
            FROM timescaledb_information.chunks
            WHERE hypertable_schema = 'public'
              AND hypertable_name = 'source_track_plays'
              AND range_end > legacy_min
              AND range_start <= legacy_max
            ORDER BY range_start
        LOOP
            IF previous_end IS NULL AND chunk.range_start > legacy_min THEN
                SELECT EXISTS (
                    SELECT 1
                    FROM public.source_track_plays_old
                    WHERE created_at >= legacy_min
                      AND created_at < chunk.range_start
                ) INTO has_uncovered_rows;
                IF has_uncovered_rows THEN
                    RAISE EXCEPTION 'current hypertable starts after legacy data at %', legacy_min;
                END IF;
            END IF;
            IF previous_end IS NOT NULL AND chunk.range_start <> previous_end THEN
                SELECT EXISTS (
                    SELECT 1
                    FROM public.source_track_plays_old
                    WHERE created_at >= previous_end
                      AND created_at < chunk.range_start
                ) INTO has_uncovered_rows;
                IF has_uncovered_rows THEN
                    RAISE EXCEPTION 'legacy rows exist in current hypertable gap between % and %',
                        previous_end, chunk.range_start;
                END IF;
            END IF;

            EXECUTE format($sql$
                SELECT
                    count(*),
                    count(*) FILTER (
                        WHERE current.id IS NULL
                           OR (
                                current.source_track_uuid,
                                current.user_uuid,
                                current.app_type
                              ) IS DISTINCT FROM (
                                legacy.source_track_uuid,
                                legacy.user_uuid,
                                legacy.app_type
                              )
                    )
                FROM public.source_track_plays_old legacy
                LEFT JOIN %I.%I current
                  ON current.id = legacy.id
                 AND current.created_at = legacy.created_at
                WHERE legacy.created_at >= $1
                  AND legacy.created_at < $2
            $sql$, chunk.chunk_schema, chunk.chunk_name)
            INTO chunk_rows, mismatched_rows
            USING chunk.range_start, chunk.range_end;

            IF mismatched_rows <> 0 THEN
                RAISE EXCEPTION '% legacy rows are missing or different in chunk %',
                    mismatched_rows, chunk.chunk_name;
            END IF;

            checked_rows := checked_rows + chunk_rows;
            checked_chunks := checked_chunks + 1;
            previous_end := chunk.range_end;

            IF checked_chunks % 250 = 0 THEN
                RAISE NOTICE 'verified % rows across % chunks', checked_rows, checked_chunks;
            END IF;
        END LOOP;

        IF previous_end IS NULL THEN
            RAISE EXCEPTION 'current hypertable has no chunks overlapping legacy data';
        END IF;
        IF previous_end <= legacy_max THEN
            SELECT EXISTS (
                SELECT 1
                FROM public.source_track_plays_old
                WHERE created_at >= previous_end
                  AND created_at <= legacy_max
            ) INTO has_uncovered_rows;
            IF has_uncovered_rows THEN
                RAISE EXCEPTION 'current hypertable does not cover legacy maximum timestamp %', legacy_max;
            END IF;
        END IF;

        RAISE NOTICE 'coverage verified: % legacy rows across % chunks', checked_rows, checked_chunks;
    END
    $verify_legacy_coverage$;
  \endif
\endif

-- Phase 1: indexes supporting the direct venue predicate and month/day show
-- lookup. Invoke on the primary with -v apply_relational_indexes=1.
\if :{?apply_relational_indexes}
  \if :apply_relational_indexes
    \echo 'Creating follow-up shows indexes concurrently'
    CREATE INDEX CONCURRENTLY idx_shows_artist_id_venue_id
        ON public.shows (artist_id, venue_id);
    CREATE INDEX CONCURRENTLY idx_shows_month_day
        ON public.shows ((EXTRACT(month FROM date)), (EXTRACT(day FROM date)));
    ANALYZE public.shows;
  \endif
\endif

-- Phase 2: the unique index has the same key columns and can serve every read
-- handled by the non-unique duplicate. Invoke with
-- -v retire_duplicate_show_index=1.
\if :{?retire_duplicate_show_index}
  \if :retire_duplicate_show_index
    \echo 'Dropping duplicate shows artist/display-date index concurrently'
    DROP INDEX CONCURRENTLY IF EXISTS public.idx_shows_artist_id_display_date;
  \endif
\endif

-- Phase 3: exact duplicate continuous-aggregate indexes. Keep the duplicate
-- family members that currently receive scans. Timescale drops their child
-- indexes with the parent; this is not concurrent, so use a short lock timeout.
-- Invoke in a controlled window with -v drop_duplicate_cagg_indexes=1.
\if :{?drop_duplicate_cagg_indexes}
  \if :drop_duplicate_cagg_indexes
    \echo 'Dropping exact duplicate continuous-aggregate index families'
    SET lock_timeout = '15s';
    DROP INDEX IF EXISTS _timescaledb_internal._materialized_hypertable_2_play_hour_show_uuid_idx;
    DROP INDEX IF EXISTS _timescaledb_internal._materialized_hypertable_2_play_hour_show_uuid_idx1;
    DROP INDEX IF EXISTS _timescaledb_internal._materialized_hypertable_2_play_hour_artist_uuid_idx;
    DROP INDEX IF EXISTS _timescaledb_internal._materialized_hypertable_3_play_day_show_uuid_idx;
    DROP INDEX IF EXISTS _timescaledb_internal._materialized_hypertable_3_play_day_artist_uuid_idx;
    RESET lock_timeout;
  \endif
\endif

-- Phase 4: remove the fully superseded pre-Timescale table and its derived
-- objects. Before invoking this phase, run the exact coverage proof above and
-- independently confirm Hangfire has no legacy refresh jobs. The explicit
-- legacy_coverage_verified variable prevents an accidental ungated drop.
-- Invoke on the primary with:
--   -v drop_legacy_track_play_storage=1 -v legacy_coverage_verified=1
\if :{?drop_legacy_track_play_storage}
  \if :drop_legacy_track_play_storage
    \if :{?legacy_coverage_verified}
      \if :legacy_coverage_verified
        \echo 'Dropping superseded source-track-play materialized views, view, and table'
        SET lock_timeout = '15s';
        ALTER SEQUENCE public.source_track_plays_id_seq
            OWNED BY public.source_track_plays.id;
        DROP MATERIALIZED VIEW IF EXISTS public.source_track_plays_by_hour_48h;
        DROP MATERIALIZED VIEW IF EXISTS public.source_track_plays_by_day_6mo;
        DROP VIEW IF EXISTS public.source_track_plays_enriched;
        DROP TABLE IF EXISTS public.source_track_plays_old;
        RESET lock_timeout;
      \else
        \echo 'ERROR: legacy_coverage_verified must be 1'
        SELECT current_setting('relisten.legacy_coverage_must_be_verified');
      \endif
    \else
      \echo 'ERROR: legacy_coverage_verified must be provided'
      SELECT current_setting('relisten.legacy_coverage_must_be_verified');
    \endif
  \endif
\endif

-- Final verification always runs.
SELECT
    c.relname AS index_name,
    i.indisready,
    i.indisvalid,
    pg_size_pretty(pg_relation_size(c.oid)) AS size,
    pg_get_indexdef(c.oid) AS definition
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname IN (
    'idx_shows_artist_id_venue_id',
    'idx_shows_month_day',
    'idx_shows_artist_id_display_date',
    'shows_artist_id_display_date_key'
)
ORDER BY c.relname;

SELECT
    to_regclass('public.source_track_plays_old') AS legacy_table,
    to_regclass('public.source_track_plays_by_hour_48h') AS legacy_hourly_view,
    to_regclass('public.source_track_plays_by_day_6mo') AS legacy_daily_view,
    to_regclass('public.source_track_plays_enriched') AS legacy_enriched_view,
    pg_size_pretty(pg_database_size(current_database())) AS database_size;
