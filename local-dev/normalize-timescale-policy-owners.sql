-- pg_restore --no-owner does not rewrite the regrole values stored in
-- TimescaleDB's policy catalog. Verify the exact production policy set before
-- replacing it; a new or changed policy must not be silently discarded.
DO $$
DECLARE
    app_job_count integer;
    expected_app_job_count integer;
BEGIN
    SELECT count(*)
      INTO app_job_count
      FROM timescaledb_information.jobs
     WHERE owner::text = 'app';

    SELECT count(*)
      INTO expected_app_job_count
      FROM timescaledb_information.jobs
     WHERE owner::text = 'app'
       AND (
         (proc_name = 'policy_refresh_continuous_aggregate'
          AND schedule_interval = interval '1 hour'
          AND config->>'mat_hypertable_id' = '2'
          AND config->>'start_offset' = '06:00:00'
          AND config->>'end_offset' = '01:00:00')
         OR
         (proc_name = 'policy_refresh_continuous_aggregate'
          AND schedule_interval = interval '1 day'
          AND config->>'mat_hypertable_id' = '3'
          AND config->>'start_offset' = '3 days'
          AND config->>'end_offset' = '1 day')
         OR
         (proc_name = 'policy_compression'
          AND schedule_interval = interval '12 hours'
          AND config->>'hypertable_id' = '1'
          AND config->>'compress_after' = '30 days')
       );

    IF app_job_count <> 3 OR expected_app_job_count <> 3 THEN
        RAISE EXCEPTION
            'TimescaleDB policy set changed: found % app-owned jobs, % recognized',
            app_job_count,
            expected_app_job_count;
    END IF;
END
$$;

SELECT remove_continuous_aggregate_policy(
    'public.source_track_plays_hourly', if_exists => true);
SELECT remove_continuous_aggregate_policy(
    'public.source_track_plays_daily', if_exists => true);
CALL remove_columnstore_policy(
    'public.source_track_plays', if_exists => true);

SELECT add_continuous_aggregate_policy(
    'public.source_track_plays_hourly',
    start_offset => interval '6 hours',
    end_offset => interval '1 hour',
    schedule_interval => interval '1 hour');
SELECT add_continuous_aggregate_policy(
    'public.source_track_plays_daily',
    start_offset => interval '3 days',
    end_offset => interval '1 day',
    schedule_interval => interval '1 day');
CALL add_columnstore_policy(
    'public.source_track_plays',
    after => interval '30 days',
    schedule_interval => interval '12 hours');

DO $$
DECLARE
    expected_relisten_job_count integer;
    remaining_app_job_count integer;
BEGIN
    SELECT count(*)
      INTO expected_relisten_job_count
      FROM timescaledb_information.jobs
     WHERE owner::text = 'relisten'
       AND (
         (proc_name = 'policy_refresh_continuous_aggregate'
          AND config->>'mat_hypertable_id' IN ('2', '3'))
         OR
         (proc_name = 'policy_compression'
          AND config->>'hypertable_id' = '1')
       );

    SELECT count(*)
      INTO remaining_app_job_count
      FROM timescaledb_information.jobs
     WHERE owner::text = 'app';

    IF expected_relisten_job_count <> 3 OR remaining_app_job_count <> 0 THEN
        RAISE EXCEPTION
            'Policy owner normalization failed: % relisten jobs, % app jobs',
            expected_relisten_job_count,
            remaining_app_job_count;
    END IF;
END
$$;

DROP ROLE app;
