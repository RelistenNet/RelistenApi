-- A play started from a playlist carries that playlist's typed User Service ID
-- (playlist_...). The catalog treats it as an opaque label: it never reads
-- user_data, and the User Service shows totals only for public playlists.
--
-- Adding a nullable column without a default changes only the catalog, also on
-- compressed chunks, but it needs a brief exclusive lock on the hypertable that
-- every play insert uses. Fail after five seconds rather than queue inserts
-- behind a long reader; the next API start or `--migrate` retries.
SET LOCAL lock_timeout = '5s';

ALTER TABLE source_track_plays ADD COLUMN IF NOT EXISTS playlist_id text;

-- Hourly totals per playlist, the source of the User Service's "most played"
-- ordering. Earlier plays have no playlist, so there is no history to backfill
-- and the aggregate starts empty. It reads materialized buckets only: without a
-- watermark, a real-time tail would scan the whole hypertable. Timescale's
-- default group index covers (playlist_id, play_hour DESC). A play becomes
-- visible within about two and a half hours: its hour must end, then the
-- one-hour end offset pass, then the next half-hourly policy run.
CREATE MATERIALIZED VIEW IF NOT EXISTS source_track_plays_playlist_hourly
WITH (timescaledb.continuous, timescaledb.materialized_only = true)
AS
SELECT time_bucket(INTERVAL '1 hour', p.created_at) AS play_hour,
       p.playlist_id,
       count(*) AS plays,
       sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON t.uuid = p.source_track_uuid
WHERE p.playlist_id IS NOT NULL
GROUP BY 1, 2
WITH NO DATA;

SELECT add_continuous_aggregate_policy(
    'source_track_plays_playlist_hourly',
    start_offset => INTERVAL '1 day',
    end_offset => INTERVAL '1 hour',
    schedule_interval => INTERVAL '30 minutes',
    if_not_exists => true
);
