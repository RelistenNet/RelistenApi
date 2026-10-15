-- Local playback starts empty. Build the same Timescale query interfaces used
-- by the API without copying listeners, playback history, or production jobs.
SET search_path = public;
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE source_track_plays (
    id serial NOT NULL,
    created_at timestamptz DEFAULT timezone('utc', now()) NOT NULL,
    source_track_uuid uuid,
    user_uuid uuid,
    app_type smallint NOT NULL,
    UNIQUE (id, created_at)
);
SELECT create_hypertable('source_track_plays', 'created_at', chunk_time_interval => interval '1 day');

CREATE MATERIALIZED VIEW source_track_plays_hourly WITH (timescaledb.continuous) AS
SELECT time_bucket('1 hour', p.created_at) AS play_hour,
       p.source_track_uuid, a.uuid AS artist_uuid, s.uuid AS source_uuid, sh.uuid AS show_uuid,
       count(*) AS plays, sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON t.uuid = p.source_track_uuid
JOIN artists a ON a.id = t.artist_id
JOIN sources s ON s.id = t.source_id
JOIN shows sh ON sh.id = s.show_id
GROUP BY 1, 2, 3, 4, 5
WITH NO DATA;

CREATE MATERIALIZED VIEW source_track_plays_daily WITH (timescaledb.continuous) AS
SELECT time_bucket('1 day', p.created_at) AS play_day,
       p.source_track_uuid, a.uuid AS artist_uuid, s.uuid AS source_uuid, sh.uuid AS show_uuid,
       count(*) AS plays, sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON t.uuid = p.source_track_uuid
JOIN artists a ON a.id = t.artist_id
JOIN sources s ON s.id = t.source_id
JOIN shows sh ON sh.id = s.show_id
GROUP BY 1, 2, 3, 4, 5
WITH NO DATA;

CREATE INDEX ON source_track_plays_hourly (play_hour DESC, artist_uuid);
CREATE INDEX ON source_track_plays_hourly (play_hour DESC, show_uuid);
CREATE INDEX ON source_track_plays_daily (play_day DESC, artist_uuid);
CREATE INDEX ON source_track_plays_daily (play_day DESC, show_uuid);

-- These are fresh local policies, with no production role or job state.
SELECT add_continuous_aggregate_policy('source_track_plays_hourly',
    start_offset => interval '6 hours', end_offset => interval '1 hour', schedule_interval => interval '1 hour');
SELECT add_continuous_aggregate_policy('source_track_plays_daily',
    start_offset => interval '3 days', end_offset => interval '1 day', schedule_interval => interval '1 day');
