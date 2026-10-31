-- Derived popularity is published by refresh jobs. Readers never aggregate plays.
-- Global and artist-local percentiles are separate populations, even for one show.
CREATE TABLE popularity_refreshes (
    kind text NOT NULL CHECK (kind IN ('artist', 'show', 'year')),
    scope_uuid uuid NOT NULL,
    generated_at timestamptz NOT NULL,
    PRIMARY KEY (kind, scope_uuid)
);
COMMENT ON COLUMN popularity_refreshes.scope_uuid IS
    'Zero UUID is the global population; otherwise the artist population. This is a refresh key, not a catalog reference.';

CREATE TABLE artist_popularity (
    uuid uuid NOT NULL REFERENCES artists(uuid) ON DELETE CASCADE,
    plays_30d bigint NOT NULL,
    hours_30d double precision NOT NULL,
    hot_score_30d double precision NOT NULL,
    plays_7d bigint NOT NULL,
    hours_7d double precision NOT NULL,
    hot_score_7d double precision NOT NULL,
    plays_48h bigint NOT NULL,
    hours_48h double precision NOT NULL,
    hot_score_48h double precision NOT NULL,
    trend_ratio double precision NOT NULL,
    momentum_score double precision NOT NULL,
    PRIMARY KEY (uuid)
);
CREATE INDEX artist_popularity_hot_30d_idx ON artist_popularity (hot_score_30d DESC, plays_30d DESC, uuid);
CREATE INDEX artist_popularity_trending_idx ON artist_popularity (trend_ratio DESC, hot_score_30d DESC, uuid);

CREATE TABLE show_popularity (
    uuid uuid NOT NULL REFERENCES shows(uuid) ON DELETE CASCADE,
    artist_uuid uuid NOT NULL REFERENCES artists(uuid) ON DELETE CASCADE,
    is_global boolean NOT NULL,
    plays_30d bigint NOT NULL,
    hours_30d double precision NOT NULL,
    hot_score_30d double precision NOT NULL,
    plays_7d bigint NOT NULL,
    hours_7d double precision NOT NULL,
    hot_score_7d double precision NOT NULL,
    plays_48h bigint NOT NULL,
    hours_48h double precision NOT NULL,
    hot_score_48h double precision NOT NULL,
    trend_ratio double precision NOT NULL,
    momentum_score double precision NOT NULL,
    organic_momentum_score double precision NULL,
    PRIMARY KEY (uuid, is_global)
);
CREATE INDEX show_popularity_artist_idx ON show_popularity (artist_uuid, is_global);
CREATE INDEX show_popularity_hot_30d_idx ON show_popularity (hot_score_30d DESC, plays_30d DESC, uuid) WHERE is_global;
CREATE INDEX show_popularity_hot_7d_idx ON show_popularity (hot_score_7d DESC, plays_7d DESC, uuid) WHERE is_global;
CREATE INDEX show_popularity_hot_48h_idx ON show_popularity (hot_score_48h DESC, plays_48h DESC, uuid) WHERE is_global;

CREATE TABLE year_popularity (
    uuid uuid NOT NULL REFERENCES years(uuid) ON DELETE CASCADE,
    artist_uuid uuid NOT NULL REFERENCES artists(uuid) ON DELETE CASCADE,
    is_global boolean NOT NULL,
    plays_30d bigint NOT NULL,
    hours_30d double precision NOT NULL,
    hot_score_30d double precision NOT NULL,
    plays_7d bigint NOT NULL,
    hours_7d double precision NOT NULL,
    hot_score_7d double precision NOT NULL,
    plays_48h bigint NOT NULL,
    hours_48h double precision NOT NULL,
    hot_score_48h double precision NOT NULL,
    trend_ratio double precision NOT NULL,
    momentum_score double precision NOT NULL,
    PRIMARY KEY (uuid, is_global)
);
CREATE INDEX year_popularity_artist_idx ON year_popularity (artist_uuid, is_global);
CREATE INDEX year_popularity_hot_30d_idx ON year_popularity (hot_score_30d DESC, plays_30d DESC, uuid) WHERE is_global;
CREATE INDEX year_popularity_trending_idx ON year_popularity (trend_ratio DESC, hot_score_30d DESC, uuid) WHERE is_global;

