-- RelistenNet/string-theory ddl/002_kb.sql (WS-5 revision, 2026-10-19), copied verbatim.

-- The song catalogue (docs/catalogue.md). Revised 2026-10-19 (WS-5): plain tables are the
-- source of truth, replacing the claims, predicate registry and projections ratified with
-- WS-0. Every row says who set it (provenance columns), and one trigger appends every
-- change to kb.changes, which gives history, blame and "revert run X".
--
-- The rules that survive from docs/edits.md:
-- - A machine (importer, algorithm, llm) never overwrites or deletes a row a person set;
--   kb.guard_people refuses it. Machines that disagree with a person report it instead.
-- - A write that changes nothing is not a change: writers update only when a value differs,
--   so re-running a bot logs nothing.
-- - Nothing a person said is lost: kb.changes keeps the before and after of every change.
--
-- Venues, people and events get tables in the same pattern when the work that fills them
-- lands (WS-8, WS-3); catalogue.md sketches them.

CREATE SCHEMA IF NOT EXISTS kb;

-- One batch of writes by one author: an import, a matcher or LLM run, a person's edit.
CREATE TABLE kb.runs (
    uuid        uuid PRIMARY KEY,
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,                  -- name@version (+ params hash or model), or the user uuid
    note        text,
    started_at  timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz
);

-- Provenance, on every catalogue row: author_type, author_ref, confidence (required for
-- machines, which are gated on it; people are 1 by convention), run_uuid, updated_at.

-- Songs are per artist (decided 2026-10-19): the same composition under two artists is two
-- songs. A merged song keeps its row and points at the survivor; readers follow merged_into.
CREATE TABLE kb.songs (
    uuid            uuid PRIMARY KEY,           -- uuid v7, minted in the app
    artist_uuid     uuid NOT NULL,              -- public.artists.uuid
    name            text NOT NULL,
    slug            text NOT NULL,              -- display string, never an id
    kind            text CHECK (kind IN ('song', 'jam', 'segment', 'tuning', 'banter', 'other')),  -- NULL = unknown
    original_artist text,                       -- cover credit
    merged_into     uuid,                       -- the surviving song, of the same artist
    author_type     text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref      text NOT NULL,
    confidence      real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid        uuid NOT NULL REFERENCES kb.runs (uuid),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (artist_uuid, slug),
    UNIQUE (artist_uuid, uuid),                 -- target of per-artist references (aliases, merges)
    FOREIGN KEY (artist_uuid, merged_into) REFERENCES kb.songs (artist_uuid, uuid),
    CHECK (merged_into IS DISTINCT FROM uuid),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL)
);
CREATE INDEX ON kb.songs (artist_uuid);

-- The lookup every title match uses: a normalised spelling (TitleNormalizer) names one song
-- per artist. "Saturday Night", "One More Saturday Nite" and "OMSN" all point at one song.
CREATE TABLE kb.song_aliases (
    artist_uuid uuid NOT NULL,
    normalized  text NOT NULL,
    alias       text NOT NULL,                  -- as first seen
    song_uuid   uuid NOT NULL,
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid    uuid NOT NULL REFERENCES kb.runs (uuid),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (artist_uuid, normalized),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    -- an artist's spelling can only name that artist's song
    FOREIGN KEY (artist_uuid, song_uuid) REFERENCES kb.songs (artist_uuid, uuid)
);
CREATE INDEX ON kb.song_aliases (song_uuid);

-- Upstream songs (public.setlist_songs, from every importer; never edited) mapped onto one
-- catalogue song each.
CREATE TABLE kb.song_evidence (
    setlist_song_uuid uuid PRIMARY KEY,         -- public.setlist_songs.uuid; no FK: upstream rows get deleted
    song_uuid         uuid NOT NULL REFERENCES kb.songs (uuid),
    author_type       text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref        text NOT NULL,
    confidence        real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid          uuid NOT NULL REFERENCES kb.runs (uuid),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL)
);
CREATE INDEX ON kb.song_evidence (song_uuid);

-- What a track's title says it holds, one row per part in order (docs/matching.md T1).
-- song_uuid NULL = not a song (tuning, crowd) or not matched; text keeps the part as titled.
-- soundcheck: played at soundcheck, the same day but not part of the show ("(Check) Harpua").
-- A track's parts are written together, so they share provenance. A medley whose parts are
-- partly known keeps the known ones; the rest have resolved = false, and later runs may fill
-- them in without that counting as changing a settled fact.
CREATE TABLE kb.track_songs (
    track_uuid  uuid NOT NULL,                  -- public.source_tracks.uuid; no FK
    part        integer NOT NULL CHECK (part >= 1),
    song_uuid   uuid REFERENCES kb.songs (uuid),
    text        text,
    segue_after boolean NOT NULL DEFAULT false,
    resolved    boolean NOT NULL DEFAULT true,   -- false: a part nobody has placed yet; the track stays open
    qualifiers  text[] NOT NULL DEFAULT '{}'
                CHECK (qualifiers <@ ARRAY['reprise', 'partial', 'unfinished', 'instrumental', 'acoustic',
                                           'with_guest', 'continued', 'tape_flip', 'soundcheck']),
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid    uuid NOT NULL REFERENCES kb.runs (uuid),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (track_uuid, part),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL)
);
CREATE INDEX ON kb.track_songs (song_uuid) WHERE song_uuid IS NOT NULL;

-- Every insert, update and delete on a catalogue table, append-only. row_key is the row's
-- primary key. The author is the transaction's (SET LOCAL kb.author_type / kb.author_ref /
-- kb.run_uuid, which writers always set), else the new row's: a moderator reverting a run
-- is logged as the moderator, though the restored rows keep their original provenance.
CREATE TABLE kb.changes (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    table_name  text NOT NULL,
    row_key     jsonb NOT NULL,
    op          text NOT NULL CHECK (op IN ('insert', 'update', 'delete')),
    before      jsonb,
    after       jsonb,
    author_type text,
    author_ref  text,
    run_uuid    uuid,
    at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON kb.changes (table_name, row_key);
CREATE INDEX ON kb.changes (run_uuid) WHERE run_uuid IS NOT NULL;

CREATE FUNCTION kb.changes_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'kb.changes is append-only';
END $$;
CREATE TRIGGER changes_immutable BEFORE UPDATE OR DELETE ON kb.changes
    FOR EACH ROW EXECUTE FUNCTION kb.changes_immutable();

-- Appends one kb.changes row. Trigger arguments name the primary key columns.
CREATE FUNCTION kb.log_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    old_row jsonb := CASE WHEN TG_OP IN ('UPDATE', 'DELETE') THEN to_jsonb(OLD) END;
    new_row jsonb := CASE WHEN TG_OP IN ('INSERT', 'UPDATE') THEN to_jsonb(NEW) END;
    key     jsonb;
BEGIN
    SELECT jsonb_object_agg(k, coalesce(new_row, old_row) -> k) INTO key FROM unnest(TG_ARGV) AS k;
    INSERT INTO kb.changes (table_name, row_key, op, before, after, author_type, author_ref, run_uuid)
    VALUES (TG_TABLE_NAME, key, lower(TG_OP), old_row, new_row,
            coalesce(nullif(current_setting('kb.author_type', true), ''), new_row ->> 'author_type'),
            coalesce(nullif(current_setting('kb.author_ref', true), ''), new_row ->> 'author_ref'),
            coalesce(nullif(current_setting('kb.run_uuid', true), '')::uuid, (new_row ->> 'run_uuid')::uuid));
    RETURN NULL;
END $$;

-- People's rows are theirs: a machine may not overwrite or delete them. The actor is the
-- transaction's kb.author_type (a moderator restoring a machine's value is still a person),
-- else for an update the new row's; a delete with no actor set is refused.
CREATE FUNCTION kb.guard_people() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    actor text := coalesce(nullif(current_setting('kb.author_type', true), ''),
                           CASE WHEN TG_OP = 'UPDATE' THEN NEW.author_type END);
BEGIN
    -- Every delete names its actor and a run of that actor, so the change log attributes it
    -- and a revert of the run finds it.
    IF TG_OP = 'DELETE' AND NOT EXISTS (
           SELECT 1 FROM kb.runs r
           WHERE r.uuid = nullif(current_setting('kb.run_uuid', true), '')::uuid
             AND r.author_type = actor
             AND r.author_ref = nullif(current_setting('kb.author_ref', true), '')) THEN
        RAISE EXCEPTION 'deleting from % needs SET LOCAL kb.author_type, kb.author_ref and kb.run_uuid naming one run', TG_TABLE_NAME;
    END IF;
    IF OLD.author_type IN ('user', 'moderator') AND actor IS DISTINCT FROM 'user'
       AND actor IS DISTINCT FROM 'moderator' THEN
        RAISE EXCEPTION 'a % row was set by a person; % may not change it', TG_TABLE_NAME, coalesce(actor, 'an unnamed writer');
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

CREATE TRIGGER songs_guard BEFORE UPDATE OR DELETE ON kb.songs
    FOR EACH ROW EXECUTE FUNCTION kb.guard_people();
CREATE TRIGGER songs_log AFTER INSERT OR UPDATE OR DELETE ON kb.songs
    FOR EACH ROW EXECUTE FUNCTION kb.log_change('uuid');
CREATE TRIGGER song_aliases_guard BEFORE UPDATE OR DELETE ON kb.song_aliases
    FOR EACH ROW EXECUTE FUNCTION kb.guard_people();
CREATE TRIGGER song_aliases_log AFTER INSERT OR UPDATE OR DELETE ON kb.song_aliases
    FOR EACH ROW EXECUTE FUNCTION kb.log_change('artist_uuid', 'normalized');
CREATE TRIGGER song_evidence_guard BEFORE UPDATE OR DELETE ON kb.song_evidence
    FOR EACH ROW EXECUTE FUNCTION kb.guard_people();
CREATE TRIGGER song_evidence_log AFTER INSERT OR UPDATE OR DELETE ON kb.song_evidence
    FOR EACH ROW EXECUTE FUNCTION kb.log_change('setlist_song_uuid');
CREATE TRIGGER track_songs_guard BEFORE UPDATE OR DELETE ON kb.track_songs
    FOR EACH ROW EXECUTE FUNCTION kb.guard_people();
CREATE TRIGGER track_songs_log AFTER INSERT OR UPDATE OR DELETE ON kb.track_songs
    FOR EACH ROW EXECUTE FUNCTION kb.log_change('track_uuid', 'part');
