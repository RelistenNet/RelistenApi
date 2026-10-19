-- The subset of RelistenNet/string-theory ddl/002_kb.sql @ b1af387 (WS-0, ratified) that reality
-- needs, copied verbatim: entities, predicates, edit_sessions, bot_runs, claims, and the
-- current_source_events and current_event_shows projections. WS-5 adds the rest (claim_reviews,
-- redirects, current_series_events) in its own migration.

-- WS-0, ratified 2026-10-17. Knowledge-base core: entities, predicates, claims, reviews, redirects.
-- Every fact is a claim (docs/edits.md). Claims are immutable; nothing here is updated in
-- place except projections. Conventions: docs/data-model.md §7, §8, §10.

CREATE SCHEMA IF NOT EXISTS kb;

-- Registry of minted catalogue entities. Attributes live in claims; `status` and
-- `split_from` are a projection of merge/split claims, kept here for resolver speed.
CREATE TABLE kb.entities (
    uuid       uuid PRIMARY KEY,
    type       text NOT NULL CHECK (type IN ('event', 'series', 'song', 'venue', 'person', 'capture', 'transfer')),
    status     text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'merged')),
    split_from uuid REFERENCES kb.entities (uuid),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- The predicate registry. A breaking value change is a new schema_version row plus an
-- upcaster in code; old claims are never rewritten.
-- Keyed by subject type: song.kind and series.kind are different predicates.
CREATE TABLE kb.predicates (
    subject_type      text NOT NULL,
    name              text NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]*$'),
    schema_version    integer NOT NULL CHECK (schema_version >= 1),
    value_schema      jsonb NOT NULL,           -- JSON Schema for the claim value
    risk              text NOT NULL CHECK (risk IN ('low', 'medium', 'high', 'structural')),
    resolution_policy text NOT NULL DEFAULT 'default',
    auto_threshold    real,                     -- machine claims below this don't resolve
    created_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (subject_type, name, schema_version)
);

CREATE TABLE kb.edit_sessions (
    uuid        uuid PRIMARY KEY,
    author_type text NOT NULL CHECK (author_type IN ('user', 'moderator')),
    author_ref  text NOT NULL,
    summary     text,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- One bulk run of an algorithm or LLM job. "Revert run X" supersedes its claims.
CREATE TABLE kb.bot_runs (
    uuid         uuid PRIMARY KEY,
    author_type  text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm')),
    author_ref   text NOT NULL,
    mode         text NOT NULL CHECK (mode IN ('dry_run', 'live')),
    started_at   timestamptz NOT NULL DEFAULT now(),
    finished_at  timestamptz,
    activated_by uuid                           -- moderator claim that activated a dry run
);

-- relisten.claim/1 (schemas/relisten.claim-1.schema.json), one immutable row each.
-- No FK on subject_uuid: subjects include derived performances (data-model.md §10).
CREATE TABLE kb.claims (
    uuid                  uuid PRIMARY KEY,
    subject_type          text NOT NULL CHECK (subject_type IN
                              ('event', 'series', 'performance', 'song', 'venue', 'person', 'capture', 'transfer', 'source', 'track')),
    subject_uuid          uuid NOT NULL,
    predicate             text NOT NULL,
    value_schema_version  integer NOT NULL,
    value                 jsonb NOT NULL,
    author_type           text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref            text NOT NULL,
    confidence            real CHECK (confidence BETWEEN 0 AND 1),
    evidence              jsonb,
    anchor                jsonb,                -- relisten.anchor/1
    anchor_md5            text GENERATED ALWAYS AS (anchor ->> 'md5') STORED,
    observed_event_uuid   uuid,
    observed_version      integer,
    supersedes            uuid REFERENCES kb.claims (uuid),
    edit_session_uuid     uuid REFERENCES kb.edit_sessions (uuid),
    bot_run_uuid          uuid REFERENCES kb.bot_runs (uuid),
    created_at            timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (subject_type, predicate, value_schema_version)
        REFERENCES kb.predicates (subject_type, name, schema_version),
    CHECK ((observed_event_uuid IS NULL) = (observed_version IS NULL)),
    CHECK (author_type NOT IN ('user', 'moderator') OR edit_session_uuid IS NOT NULL),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR (bot_run_uuid IS NOT NULL AND confidence IS NOT NULL))
);
CREATE INDEX ON kb.claims (subject_uuid, predicate, created_at DESC);
CREATE INDEX ON kb.claims (supersedes) WHERE supersedes IS NOT NULL;
CREATE INDEX ON kb.claims (bot_run_uuid) WHERE bot_run_uuid IS NOT NULL;
CREATE INDEX ON kb.claims (anchor_md5) WHERE anchor_md5 IS NOT NULL;
-- Bot idempotency lookup: same subject, predicate, author and value → no new claim.
CREATE INDEX ON kb.claims (subject_uuid, predicate, author_ref) WHERE author_type IN ('importer', 'algorithm', 'llm');

CREATE FUNCTION kb.claims_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'kb.claims is append-only; write a superseding claim instead';
END $$;
CREATE TRIGGER claims_immutable BEFORE UPDATE OR DELETE ON kb.claims
    FOR EACH ROW EXECUTE FUNCTION kb.claims_immutable();

-- Events, series and sources (docs/catalogue.md §Events vs shows). Which sources an event
-- holds, which series it belongs to and which legacy show it is filed under are claims
-- (source.event, event.series, event.legacy_show); these tables are their resolved
-- projections, rebuildable from kb.claims like kb.redirects. Reality builds read
-- current_source_events to assemble an event's inputs manifest.

-- A source's audio belongs to one or more events. md5s NULL = the whole source; otherwise
-- only those files (a transfer holding both the early and the late show is in both events,
-- each with its own files; a file straddling the two is listed in both).
CREATE TABLE kb.current_source_events (
    source_uuid uuid NOT NULL,                  -- public.sources.uuid; no FK: sources get deleted
    event_uuid  uuid NOT NULL REFERENCES kb.entities (uuid),
    md5s        text[] CHECK (md5s IS NULL OR cardinality(md5s) > 0),
    claim_uuid  uuid NOT NULL REFERENCES kb.claims (uuid),
    PRIMARY KEY (source_uuid, event_uuid)
);
CREATE INDEX ON kb.current_source_events (event_uuid);

-- The legacy show (public.shows, a deterministic uuid per date) an event is filed under,
-- so the existing API keeps serving shows: one show → n events.
CREATE TABLE kb.current_event_shows (
    event_uuid uuid PRIMARY KEY REFERENCES kb.entities (uuid),
    show_uuid  uuid NOT NULL,                   -- public.shows.uuid
    claim_uuid uuid NOT NULL REFERENCES kb.claims (uuid)
);
CREATE INDEX ON kb.current_event_shows (show_uuid);
