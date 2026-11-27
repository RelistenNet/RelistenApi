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

-- Daily totals per playlist, the source of the User Service's "most played"
-- ordering. Earlier plays have no playlist, so there is no history to backfill
-- and the aggregate starts empty. Real-time aggregation adds plays after the
-- last materialized day straight from the hypertable, so today's plays count
-- immediately. Timescale's default group index covers (playlist_id, play_day).
--
-- A refresh over empty days does not advance the materialization watermark.
-- While real-time reads are enabled, a bound parameter on play_day lets
-- Timescale exclude older raw chunks at startup.
CREATE MATERIALIZED VIEW IF NOT EXISTS source_track_plays_playlist_daily
WITH (timescaledb.continuous, timescaledb.materialized_only = false)
AS
SELECT time_bucket(INTERVAL '1 day', p.created_at) AS play_day,
       p.playlist_id,
       count(*) AS plays,
       sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON t.uuid = p.source_track_uuid
WHERE p.playlist_id IS NOT NULL
GROUP BY 1, 2
WITH NO DATA;

-- Materialize each closed day an hour after it ends, and re-close the previous
-- three days to pick up late uploads (offline plays and history imports).
SELECT add_continuous_aggregate_policy(
    'source_track_plays_playlist_daily',
    start_offset => INTERVAL '3 days',
    end_offset => INTERVAL '1 hour',
    schedule_interval => INTERVAL '1 hour',
    if_not_exists => true
);
