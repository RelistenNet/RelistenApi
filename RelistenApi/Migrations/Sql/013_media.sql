-- Copied verbatim from RelistenNet/string-theory ddl/001_media.sql @ b1af387 (WS-0, ratified).

-- WS-0, ratified 2026-10-17. Evidence about audio files, and their derived features.
-- Conventions: docs/data-model.md §10. Times are *_us bigint. uuids are v7, minted in the app.

CREATE SCHEMA IF NOT EXISTS media;

-- Every params object any algorithm ran with, stored once. params_hash =
-- first 16 hex of sha256(canonical JSON of params) (docs/formats.md).
CREATE TABLE media.algo_params (
    params_hash text PRIMARY KEY CHECK (params_hash ~ '^[0-9a-f]{16}$'),
    algo        text NOT NULL,                  -- e.g. 'align/1.0.0', 'anchor-fp/1'
    params      jsonb NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- One row per distinct audio content. The md5 is the identity (data-model.md §2).
CREATE TABLE media.files (
    md5           text PRIMARY KEY CHECK (md5 ~ '^[0-9a-f]{32}$'),
    duration_us   bigint CHECK (duration_us >= 0),
    sample_rate   integer,
    sample_count  bigint,
    decoder       text,                         -- decoder id+version of the last decode
    qa_status     text NOT NULL DEFAULT 'ok' CHECK (qa_status IN ('ok', 'quarantined', 'failed')),
    qa_flags      text[] NOT NULL DEFAULT '{}', -- 'dts_noise', 'duplicate', 'filler', ...
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at  timestamptz NOT NULL DEFAULT now()
);

-- One blob per (file, component, version, params). Blob layout: docs/formats.md.
CREATE TABLE media.features (
    md5               text NOT NULL REFERENCES media.files (md5),
    component         text NOT NULL CHECK (component IN ('peaks', 'cens', 'tuning', 'chromaprint')),
    component_version integer NOT NULL CHECK (component_version >= 1),
    params_hash       text NOT NULL REFERENCES media.algo_params (params_hash),
    blob_key          text NOT NULL,            -- features/{md5}/{component}-v{N}.bin
    blob_bytes        bigint NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (md5, component, component_version, params_hash)
);

-- Every anchor any asserted row holds, copied here when the holder is written (anchors are
-- immutable, so the copy can't drift). Two jobs:
--   * the re-landing work list: a rebuild finds every anchor on its files by md5;
--   * a GC pin: feature blobs (current component versions) of an anchored md5 are never
--     pruned, so re-matching (file_lineage) works after the audio is gone (data-model.md §2.1).
-- Comments live in the user service's database; it registers their anchors through the API.
CREATE TABLE media.anchors (
    holder_type text NOT NULL CHECK (holder_type IN ('comment', 'claim')),
    holder_uuid uuid NOT NULL,
    ordinal     smallint NOT NULL,              -- position in the holder's anchor list
    md5         text NOT NULL REFERENCES media.files (md5),
    offset_us   bigint NOT NULL,
    anchor      jsonb NOT NULL,                 -- relisten.anchor/1, verbatim
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (holder_type, holder_uuid, ordinal)
);
CREATE INDEX ON media.anchors (md5);

-- What one import saw for one source (evidence, append-only). Disappearance detection
-- compares the latest two snapshots of a source (data-model.md §2.1).
CREATE TABLE media.import_snapshots (
    uuid                uuid PRIMARY KEY,
    source_uuid         uuid NOT NULL,          -- public.sources.uuid; no FK: sources get deleted
    upstream_identifier text NOT NULL,
    importer            text NOT NULL,          -- importer name@version
    seen_at             timestamptz NOT NULL DEFAULT now(),
    -- Insert order. Breaks seen_at ties: two imports in one transaction share now(), and uuid v7s
    -- minted in the same millisecond don't sort by creation.
    seq                 bigint GENERATED ALWAYS AS IDENTITY UNIQUE
);
CREATE INDEX ON media.import_snapshots (source_uuid, seen_at DESC);

CREATE TABLE media.import_snapshot_tracks (
    snapshot_uuid  uuid NOT NULL REFERENCES media.import_snapshots (uuid),
    track_uuid     uuid NOT NULL,
    track_position integer NOT NULL,
    md5            text CHECK (md5 ~ '^[0-9a-f]{32}$'),  -- null when upstream gave none
    title          text,
    mp3_url        text,
    PRIMARY KEY (snapshot_uuid, track_uuid),
    UNIQUE (snapshot_uuid, track_position)
);
CREATE INDEX ON media.import_snapshot_tracks (md5);

-- md5 → current track(s): the lookup that makes track uuids a hint, not an identity. Every
-- track of each source's latest snapshot, one row per occurrence: a source can hold the same
-- md5 at two positions, so resolve a stored file by (source_uuid, track_position, md5), as the
-- inputs manifest and SourceFile record it (data-model.md §5), not by (source_uuid, md5).
-- Exactly one latest snapshot per source: latest seen_at, ties broken by insert order (seq).
CREATE VIEW media.current_tracks AS
WITH latest AS (
    SELECT DISTINCT ON (source_uuid) uuid, source_uuid, seen_at
    FROM media.import_snapshots
    ORDER BY source_uuid, seen_at DESC, seq DESC
)
SELECT t.md5, l.source_uuid, t.track_position, t.track_uuid, l.seen_at
FROM latest l
JOIN media.import_snapshot_tracks t ON t.snapshot_uuid = l.uuid
WHERE t.md5 IS NOT NULL;

-- Old file time → new file time when upstream bytes changed (split, join, retrim, re-encode).
-- Derived: a new algorithm run writes new rows; anchors re-point through the latest.
CREATE TABLE media.file_lineage (
    from_md5    text NOT NULL REFERENCES media.files (md5),
    to_md5      text NOT NULL REFERENCES media.files (md5),
    kind        text NOT NULL CHECK (kind IN ('split', 'join', 'retrim', 'reencode')),
    map         bytea NOT NULL,                 -- relisten.common.v1.PiecewiseLinearMap
    unmatched   bytea,                          -- repeated relisten.common.v1.Span, packed as FileLineage.unmatched
    algo        text NOT NULL,
    params_hash text NOT NULL REFERENCES media.algo_params (params_hash),
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (from_md5, to_md5, algo, params_hash),
    CHECK (from_md5 <> to_md5)
);
CREATE INDEX ON media.file_lineage (to_md5);

-- Lineage graph entities (WS-8). Identity only; every attribute (type, taper, equipment,
-- generation, chain) is a claim, projected to kb.current_*.
CREATE TABLE media.captures (
    uuid       uuid PRIMARY KEY,
    event_uuid uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE media.transfers (
    uuid        uuid PRIMARY KEY,
    source_uuid uuid,                           -- the Relisten source it was published as, if any
    created_at  timestamptz NOT NULL DEFAULT now()
);
