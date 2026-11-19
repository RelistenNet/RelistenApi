-- Backfill the new aggregates separately. Startup must not materialize years of history.
-- Real-time tails read the daily aggregate, so a completed month remains visible
-- at month turnover before the upper-level refresh has materialized it.
CREATE MATERIALIZED VIEW source_track_plays_monthly
WITH (timescaledb.continuous, timescaledb.materialized_only = false, timescaledb.create_group_indexes = false)
AS
SELECT time_bucket(INTERVAL '1 month', play_day) AS play_month,
       source_track_uuid,
       source_uuid,
       show_uuid,
       artist_uuid,
       sum(plays)::bigint AS plays,
       sum(total_track_seconds)::bigint AS total_track_seconds,
       count(*) AS days_with_plays
FROM source_track_plays_daily
GROUP BY 1, 2, 3, 4, 5
WITH NO DATA;

CREATE INDEX ON source_track_plays_monthly (play_month DESC);
CREATE INDEX ON source_track_plays_monthly (artist_uuid, play_month DESC);
CREATE INDEX ON source_track_plays_monthly (source_track_uuid, play_month DESC);

-- A source can have several tracks played on the same date. Count source-days
-- from this grain so those dates are counted once.
CREATE MATERIALIZED VIEW source_plays_daily
WITH (timescaledb.continuous, timescaledb.materialized_only = false)
AS
SELECT time_bucket(INTERVAL '1 day', play_day) AS play_day,
       source_uuid,
       sum(plays)::bigint AS plays,
       sum(total_track_seconds)::bigint AS total_track_seconds
FROM source_track_plays_daily
GROUP BY 1, 2
WITH NO DATA;

-- Close whole UTC months after the same one-day delay as the daily policy.
-- Refresh the daily input explicitly before monthly advances its watermark.
-- Each CALL may commit batches. An error stops this attempt and the next
-- attempt starts with daily again.
CREATE PROCEDURE public.refresh_source_track_plays_monthly(job_id integer, config jsonb)
LANGUAGE plpgsql AS $$
DECLARE
    month_end timestamp := date_trunc('month', (now() AT TIME ZONE 'UTC') - INTERVAL '1 day');
    window_start timestamptz := (month_end - INTERVAL '3 months') AT TIME ZONE 'UTC';
    window_end timestamptz := month_end AT TIME ZONE 'UTC';
BEGIN
    CALL public.refresh_continuous_aggregate(
        'public.source_track_plays_daily', window_start, window_end
    );
    CALL public.refresh_continuous_aggregate(
        'public.source_track_plays_monthly', window_start, window_end
    );
END;
$$;

SELECT add_job(
    'public.refresh_source_track_plays_monthly',
    schedule_interval => INTERVAL '1 day',
    initial_start => now() + INTERVAL '1 day',
    job_name => 'Refresh monthly track plays'
);

SELECT add_continuous_aggregate_policy(
    'source_plays_daily',
    start_offset => INTERVAL '7 days',
    end_offset => INTERVAL '1 day',
    schedule_interval => INTERVAL '1 day',
    initial_start => now() + INTERVAL '1 day'
);

-- NULL means the yearly window has not been published. Existing writes and
-- readers can continue during the migration.
ALTER TABLE artist_popularity
    ADD COLUMN plays_1y bigint,
    ADD COLUMN hours_1y double precision,
    ADD COLUMN hot_score_1y double precision;

ALTER TABLE show_popularity
    ADD COLUMN plays_1y bigint,
    ADD COLUMN hours_1y double precision,
    ADD COLUMN hot_score_1y double precision;

ALTER TABLE year_popularity
    ADD COLUMN plays_1y bigint,
    ADD COLUMN hours_1y double precision,
    ADD COLUMN hot_score_1y double precision;
