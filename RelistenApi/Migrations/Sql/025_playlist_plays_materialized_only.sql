-- Playlist popularity reads completed materializations. A real-time tail would
-- scan the newest raw chunk for playlist IDs, which have no index on the play
-- hypertable. The hourly policy still refreshes recently closed UTC days.
ALTER MATERIALIZED VIEW source_track_plays_playlist_daily
    SET (timescaledb.materialized_only = true);
