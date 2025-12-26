CREATE EXTENSION IF NOT EXISTS timescaledb VERSION '2.19.3';

  -- create the new empty table (same schema)
CREATE TABLE source_track_plays_new (LIKE source_track_plays INCLUDING ALL);

CREATE UNIQUE INDEX source_track_plays_new_id_created_at_uidx
    ON source_track_plays_new (id, created_at);

-- convert it to a hypertable
SELECT create_hypertable(
               'source_track_plays_new',
               'created_at',
               chunk_time_interval => interval '1 day'
       );

CREATE OR REPLACE FUNCTION source_track_plays_mirror()
  RETURNS trigger AS $$
BEGIN
INSERT INTO source_track_plays_new
VALUES (NEW.*);
RETURN NEW;
END;
  $$ LANGUAGE plpgsql;

CREATE TRIGGER trg_source_track_plays_mirror
    AFTER INSERT ON source_track_plays
    FOR EACH ROW EXECUTE FUNCTION source_track_plays_mirror();

CREATE OR REPLACE PROCEDURE backfill_source_track_plays(
    batch_size bigint,
    min_id bigint
  )
  LANGUAGE plpgsql
  AS $$
  DECLARE
max_id bigint := (SELECT max(id) FROM source_track_plays);
    cur_id bigint := min_id;
    next_id bigint;
    inserted_count bigint;
BEGIN
    WHILE cur_id <= max_id LOOP
      next_id := cur_id + batch_size - 1;

SET LOCAL synchronous_commit = off;

INSERT INTO source_track_plays (id, created_at, source_track_uuid, user_uuid, app_type)
SELECT id, created_at, source_track_uuid, user_uuid, app_type
FROM source_track_plays_old
WHERE id BETWEEN cur_id AND next_id
    ON CONFLICT (id, created_at) DO NOTHING;

GET DIAGNOSTICS inserted_count = ROW_COUNT;
RAISE NOTICE 'inserted % rows for id % -> %', inserted_count, cur_id, LEAST(next_id, max_id);

COMMIT;

cur_id := next_id + 1;
      PERFORM pg_sleep(1.0);
END LOOP;
END $$;

CALL backfill_source_track_plays(10000, 1);

BEGIN;
DROP TRIGGER trg_source_track_plays_mirror ON source_track_plays;
DROP FUNCTION source_track_plays_mirror();

ALTER TABLE source_track_plays RENAME TO source_track_plays_old;
ALTER TABLE source_track_plays_new RENAME TO source_track_plays;
COMMIT;


CREATE MATERIALIZED VIEW source_track_plays_hourly
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 hour', p.created_at) AS play_hour,
    p.source_track_uuid,
    a.uuid as artist_uuid,
    s.uuid AS source_uuid,
    sh.uuid AS show_uuid,
    count(*) AS plays,
    sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
         JOIN source_tracks t ON p.source_track_uuid = t.uuid
         JOIN artists a ON a.id = t.artist_id
         JOIN sources s ON s.id = t.source_id
         JOIN shows sh ON sh.id = s.show_id
GROUP BY 1,2,3,4,5;

CREATE INDEX ON source_track_plays_hourly (play_hour DESC, artist_uuid);
CREATE INDEX ON source_track_plays_hourly (play_hour DESC, show_uuid);

CREATE MATERIALIZED VIEW source_track_plays_daily
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 day', p.created_at) AS play_day,
    p.source_track_uuid,
    a.uuid as artist_uuid,
    s.uuid AS source_uuid,
    sh.uuid AS show_uuid,
    count(*) AS plays,
    sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
         JOIN source_tracks t ON p.source_track_uuid = t.uuid
         JOIN artists a ON a.id = t.artist_id
         JOIN sources s ON s.id = t.source_id
         JOIN shows sh ON sh.id = s.show_id
GROUP BY 1,2,3,4,5;

CREATE INDEX CONCURRENTLY ON source_track_plays_daily (play_day DESC, artist_uuid);
CREATE INDEX CONCURRENTLY ON source_track_plays_daily (play_day DESC, show_uuid);

SELECT add_continuous_aggregate_policy('source_track_plays_hourly',
                                       start_offset => interval '6 hours',
                                       end_offset => interval '1 hour',
                                       schedule_interval => interval '1 hour');

SELECT add_continuous_aggregate_policy('source_track_plays_daily',
                                       start_offset => interval '3 days',
                                       end_offset => interval '1 day',
                                       schedule_interval => interval '1 day');

SELECT min(created_at) AS min_ts, max(created_at) AS max_ts
FROM source_track_plays;

DO $$
DECLARE
start_ts timestamptz := (SELECT min(created_at) FROM source_track_plays);
    end_ts timestamptz := (SELECT max(created_at) FROM source_track_plays);
    cur_ts timestamptz;
BEGIN
    cur_ts := start_ts;
    WHILE cur_ts < end_ts LOOP
      RAISE NOTICE 'hourly backfill: % -> %', cur_ts, LEAST(cur_ts + interval '7 days', end_ts);
CALL refresh_continuous_aggregate('source_track_plays_hourly',
        cur_ts, LEAST(cur_ts + interval '7 days', end_ts));
cur_ts := cur_ts + interval '7 days';
END LOOP;
END $$;

DO $$
DECLARE
start_ts timestamptz := (SELECT min(created_at) FROM source_track_plays);
    end_ts timestamptz := (SELECT max(created_at) FROM source_track_plays);
    cur_ts timestamptz;
BEGIN
    cur_ts := start_ts;
    WHILE cur_ts < end_ts LOOP
      RAISE NOTICE 'daily backfill: % -> %', cur_ts, LEAST(cur_ts + interval '1 months', end_ts);
CALL refresh_continuous_aggregate('source_track_plays_daily',
        cur_ts, LEAST(cur_ts + interval '1 months', end_ts));
cur_ts := cur_ts + interval '1 months';
END LOOP;
END $$;

---

DO $$
DECLARE
    start_ts timestamptz := NOW() - interval '14 days';
    end_ts timestamptz := NOW();
    cur_ts timestamptz;
BEGIN
    cur_ts := start_ts;
    WHILE cur_ts < end_ts LOOP
            RAISE NOTICE 'hourly backfill: % -> %', cur_ts, LEAST(cur_ts + interval '1 days', end_ts);
            CALL refresh_continuous_aggregate('source_track_plays_hourly',
                                              cur_ts, LEAST(cur_ts + interval '1 days', end_ts));
            cur_ts := cur_ts + interval '1 days';
        END LOOP;
END $$;

DO $$
    DECLARE
        start_ts timestamptz := (SELECT min(created_at) FROM source_track_plays);
        end_ts timestamptz := (SELECT max(created_at) FROM source_track_plays);
        cur_ts timestamptz;
    BEGIN
        cur_ts := start_ts;
        WHILE cur_ts < end_ts LOOP
                RAISE NOTICE 'daily backfill: % -> %', cur_ts, LEAST(cur_ts + interval '1 months', end_ts);
                CALL refresh_continuous_aggregate('source_track_plays_daily',
                                                  cur_ts, LEAST(cur_ts + interval '1 months', end_ts));
                cur_ts := cur_ts + interval '1 months';
            END LOOP;
    END $$;
