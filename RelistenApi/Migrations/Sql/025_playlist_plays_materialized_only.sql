-- The User Service's 30-day play counts read only materialized days. Real-time
-- aggregation filled in the open day from source_track_plays, which has no
-- playlist_id index, so every listed playlist scanned the newest raw chunk.
-- A play now counts once its UTC day closes and the hourly policy (migration
-- 24) materializes it: one to 25 hours later. The listing is this aggregate's
-- only reader. Changes catalog metadata alone; idempotent.
ALTER MATERIALIZED VIEW source_track_plays_playlist_daily
    SET (timescaledb.materialized_only = true);
