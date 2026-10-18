-- Copied verbatim from RelistenNet/string-theory ddl/003_reality.sql @ b1af387 (WS-0, ratified).

-- WS-0, ratified 2026-10-17. Reality versions: derived, immutable, append-only, with a current pointer.
-- These tables are the source of truth; relisten.reality.v1.Reality is their exported view,
-- and every version is also kept forever as that message in a blob (versions.blob_key).
-- Retention (data-model.md §5): versions, builds, performances, version_sources,
-- version_maps, performance_lineage and anchor_landings are kept forever. pieces, segments,
-- gaps and unmapped are kept only for versions listed in version_rows (the current one, and
-- the previous one during the rollback window); anything else is reloaded from the blob.

CREATE SCHEMA IF NOT EXISTS reality;

CREATE TABLE reality.versions (
    event_uuid            uuid NOT NULL REFERENCES kb.entities (uuid),
    version_n             integer NOT NULL CHECK (version_n >= 1),
    schema                text NOT NULL DEFAULT 'relisten.reality/1',
    algo                  jsonb NOT NULL,       -- {"align": "1.0.0", "reality": "1.0.0"}
    params_hash           text NOT NULL REFERENCES media.algo_params (params_hash),
    inputs                jsonb NOT NULL,       -- relisten.reality-inputs/1
    inputs_hash           text NOT NULL CHECK (inputs_hash ~ '^[0-9a-f]{64}$'),
    reference_source_uuid uuid NOT NULL,
    duration_us           bigint NOT NULL,
    -- sha256 of the version's content: the Reality message with its metadata cleared
    -- (docs/formats.md §Reality blobs). A rebuild with the same content mints nothing.
    content_hash          text NOT NULL CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    blob_key              text NOT NULL,        -- reality/{event_uuid}/v{n}.binpb: relisten.reality.v1.Reality
    blob_sha256           text NOT NULL CHECK (blob_sha256 ~ '^[0-9a-f]{64}$'),
    blob_bytes            bigint NOT NULL CHECK (blob_bytes > 0),
    diagnostics           bytea,                -- relisten.reality.v1.Diagnostics
    created_at            timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (event_uuid, version_n)
);
-- Not unique: an event can return to an earlier input set (a file added, then removed), and
-- that mints a new version after the intervening one; history stays linear and append-only.
-- Skipping is decided against reality.builds of the current version (data-model.md §5).
CREATE INDEX ON reality.versions (event_uuid, inputs_hash);

-- Every build ShowReality ran, append-only, including the ones that minted nothing.
--   minted     a new version, version_n, was written
--   unchanged  the content equalled version_n (the current version), so nothing was minted
--   failed     no version; error says why
-- A rebuild is skipped before it runs when its inputs_hash matches a minted or unchanged
-- build whose version_n is current (data-model.md §5).
CREATE TABLE reality.builds (
    uuid         uuid PRIMARY KEY,
    event_uuid   uuid NOT NULL REFERENCES kb.entities (uuid),
    trigger      text NOT NULL CHECK (trigger IN ('sources_changed', 'algorithm', 'event_changed', 'manual')),
    inputs       jsonb NOT NULL,                -- relisten.reality-inputs/1
    inputs_hash  text NOT NULL CHECK (inputs_hash ~ '^[0-9a-f]{64}$'),
    algo         jsonb NOT NULL,
    params_hash  text NOT NULL REFERENCES media.algo_params (params_hash),
    outcome      text NOT NULL CHECK (outcome IN ('minted', 'unchanged', 'failed')),
    version_n    integer,
    content_hash text CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    error        text,
    stats        jsonb,                         -- run metadata, not part of any version: per-stage timings, audio_us
    created_at   timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.versions (event_uuid, version_n),
    CHECK ((outcome = 'failed') = (version_n IS NULL)),
    CHECK ((outcome = 'failed') = (content_hash IS NULL))
);
CREATE INDEX ON reality.builds (event_uuid, inputs_hash);
CREATE INDEX ON reality.builds (event_uuid, created_at DESC);

-- Versions whose row tables (pieces, segments, gaps, unmapped) are loaded. Pruning deletes a
-- version's rows and its marker in one transaction; rollback reloads them from the blob and
-- re-inserts the marker. events_current references this table, so a version can't be
-- published without its rows, and the current version's rows can't be pruned.
CREATE TABLE reality.version_rows (
    event_uuid uuid NOT NULL,
    version_n  integer NOT NULL,
    loaded_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (event_uuid, version_n),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.versions (event_uuid, version_n)
);

-- The only row that is ever updated in this schema. Swapping it is the publish step.
CREATE TABLE reality.events_current (
    event_uuid uuid PRIMARY KEY,
    version_n  integer NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.version_rows (event_uuid, version_n)
);

CREATE TABLE reality.performances (
    event_uuid     uuid NOT NULL,
    version_n      integer NOT NULL,
    uuid           uuid NOT NULL,               -- carried forward across versions
    ordinal        integer NOT NULL,
    label          text NOT NULL,               -- display only
    occurrence     integer NOT NULL,
    start_us       bigint NOT NULL,
    end_us         bigint NOT NULL CHECK (end_us > start_us),
    boundary_in    jsonb,                       -- evidence (Boundary); not queried
    title_evidence jsonb,                       -- evidence (TitleEvidence[]); not queried
    PRIMARY KEY (event_uuid, version_n, uuid),
    UNIQUE (event_uuid, version_n, ordinal),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.versions (event_uuid, version_n)
);
CREATE INDEX ON reality.performances (uuid);

CREATE TABLE reality.version_sources (
    event_uuid           uuid NOT NULL,
    version_n            integer NOT NULL,
    source_uuid          uuid NOT NULL,
    kind                 text NOT NULL CHECK (kind IN ('sbd', 'aud', 'mtx', 'fm', 'unknown')),
    is_reference         boolean NOT NULL,
    coverage             double precision NOT NULL,
    median_speed         double precision,
    tuning_cents         double precision,
    pitch_vs_slope_cents double precision,
    unmapped_music_us    bigint NOT NULL DEFAULT 0,
    orphan               boolean NOT NULL DEFAULT false,  -- SourceAlignment.orphan
    performance_coverage jsonb,                 -- [{performance_uuid, share}]
    PRIMARY KEY (event_uuid, version_n, source_uuid),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.versions (event_uuid, version_n)
);

-- Contiguous file → reality maps (Piece).
CREATE TABLE reality.pieces (
    event_uuid  uuid NOT NULL,
    version_n   integer NOT NULL,
    md5         text NOT NULL,
    ordinal     integer NOT NULL,
    source_uuid uuid NOT NULL,
    track_position integer NOT NULL,            -- SourceFile.position: with source and md5, names the occurrence
    track_uuid  uuid NOT NULL,                  -- as of the build; current track via media.current_tracks
    knots       bytea NOT NULL,                 -- relisten.common.v1.PiecewiseLinearMap
    anchors     integer NOT NULL,
    signals     jsonb NOT NULL,                 -- mean_inliers, dtw_cost, slope, ...
    -- Keyed by occurrence, not md5: an md5 can recur in an event (sources sharing files, a
    -- source holding one file twice), and each occurrence numbers its pieces from 0.
    PRIMARY KEY (event_uuid, version_n, source_uuid, track_position, ordinal),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.version_rows (event_uuid, version_n)
);

-- Pieces cut at performance boundaries: the read model for playback and comments.
-- Identity (data-model.md §2): (event, version, track_uuid, ordinal).
CREATE TABLE reality.segments (
    event_uuid       uuid NOT NULL,
    version_n        integer NOT NULL,
    track_uuid       uuid NOT NULL,
    ordinal          integer NOT NULL,
    md5              text NOT NULL,
    source_uuid      uuid NOT NULL,
    track_position   integer NOT NULL,          -- SourceFile.position: (source, position, md5) → current track
    performance_uuid uuid NOT NULL,
    file_start_us    bigint NOT NULL,
    file_end_us      bigint NOT NULL,
    reality_start_us bigint NOT NULL,
    reality_end_us   bigint NOT NULL,
    speed            double precision NOT NULL,
    signals          jsonb NOT NULL,            -- relisten.reality.v1.Signals as JSON
    knots            bytea NOT NULL,            -- relisten.common.v1.PiecewiseLinearMap
    PRIMARY KEY (event_uuid, version_n, track_uuid, ordinal),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.version_rows (event_uuid, version_n)
);
CREATE INDEX ON reality.segments (event_uuid, version_n, reality_start_us);
CREATE INDEX ON reality.segments (md5);       -- anchor landing: md5 → segments
CREATE INDEX ON reality.pieces (event_uuid, version_n, md5);
CREATE INDEX ON reality.segments (event_uuid, version_n, performance_uuid);

CREATE TABLE reality.gaps (
    event_uuid       uuid NOT NULL,
    version_n        integer NOT NULL,
    source_uuid      uuid NOT NULL,
    performance_uuid uuid NOT NULL,
    reality_start_us bigint NOT NULL,
    reality_end_us   bigint NOT NULL,
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.version_rows (event_uuid, version_n)
);
CREATE INDEX ON reality.gaps (event_uuid, version_n, source_uuid);

CREATE TABLE reality.unmapped (
    event_uuid uuid NOT NULL,
    version_n  integer NOT NULL,
    md5        text NOT NULL,
    start_us   bigint NOT NULL,                 -- file time
    end_us     bigint NOT NULL,
    level_db   double precision,
    music      boolean NOT NULL,
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.version_rows (event_uuid, version_n)
);

-- Diff from one version to the next (data-model.md §5). Kept forever.
CREATE TABLE reality.version_maps (
    event_uuid   uuid NOT NULL,
    from_version integer NOT NULL,
    to_version   integer NOT NULL,
    body         bytea NOT NULL,                -- relisten.reality.v1.VersionMap (map, removed, inserted)
    created_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (event_uuid, from_version, to_version),
    FOREIGN KEY (event_uuid, from_version) REFERENCES reality.versions (event_uuid, version_n),
    FOREIGN KEY (event_uuid, to_version) REFERENCES reality.versions (event_uuid, version_n),
    CHECK (to_version > from_version)
);

-- Where each anchor (media.anchors) lands in each version: derived, one row per
-- (anchor, version), written by the rebuild that created the version. The anchor's own
-- `seen` is frozen at writing time; this table is where re-landing results go.
-- method, in the order tried (data-model.md §6):
--   md5          the anchor's file is in this version's inputs
--   lineage      the file was split/re-encoded; landed through media.file_lineage
--   fingerprint  re-landed by anchor-fp against the same recording's files
--   version_map  no audio match; previous landing translated through reality.version_maps
--   stale        nothing worked; position is the last good landing, flagged
CREATE TABLE reality.anchor_landings (
    holder_type      text NOT NULL,
    holder_uuid      uuid NOT NULL,
    ordinal          smallint NOT NULL,
    event_uuid       uuid NOT NULL,
    version_n        integer NOT NULL,
    method           text NOT NULL CHECK (method IN ('md5', 'lineage', 'fingerprint', 'version_map', 'stale')),
    md5              text,                      -- the file it landed in (null for version_map, stale)
    offset_us        bigint,
    reality_us       bigint NOT NULL,
    performance_uuid uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (holder_type, holder_uuid, ordinal, event_uuid, version_n),
    FOREIGN KEY (event_uuid, version_n) REFERENCES reality.versions (event_uuid, version_n)
);
CREATE INDEX ON reality.anchor_landings (event_uuid, version_n, performance_uuid);

-- Performance identity edges between versions; claims follow these. Kept forever.
CREATE TABLE reality.performance_lineage (
    event_uuid   uuid NOT NULL,
    from_version integer NOT NULL,
    to_version   integer NOT NULL,
    from_uuid    uuid,
    to_uuid      uuid,
    kind         text NOT NULL CHECK (kind IN ('same', 'split', 'merge', 'new', 'gone')),
    overlap      double precision,
    FOREIGN KEY (event_uuid, from_version, to_version)
        REFERENCES reality.version_maps (event_uuid, from_version, to_version),
    CHECK (kind <> 'new' OR from_uuid IS NULL),
    CHECK (kind <> 'gone' OR to_uuid IS NULL),
    CHECK (kind IN ('new', 'gone') OR (from_uuid IS NOT NULL AND to_uuid IS NOT NULL))
);
CREATE INDEX ON reality.performance_lineage (from_uuid);
CREATE INDEX ON reality.performance_lineage (to_uuid);
