CREATE TABLE IF NOT EXISTS public.playlist_popularity (
    playlist_id text PRIMARY KEY,
    as_of timestamptz NOT NULL,
    popularity jsonb NOT NULL,
    plays_30d bigint GENERATED ALWAYS AS ((popularity #>> '{windows,30d,plays}')::bigint) STORED,
    plays_1y bigint GENERATED ALWAYS AS ((popularity #>> '{windows,1y,plays}')::bigint) STORED
);
