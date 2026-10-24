-- RelistenNet/string-theory ddl/002_norm.sql (WS-5 revision, 2026-10-24), copied verbatim.

-- The catalogue: songs, and venues since 2026-10-23 (docs/catalogue.md). Revised 2026-10-19 (WS-5): plain tables are the
-- source of truth, replacing the claims, predicate registry and projections ratified with
-- WS-0. Every row says who set it (provenance columns), and one trigger appends every
-- change to norm.changes, which gives history, blame and "revert run X".
--
-- The rules that survive from docs/edits.md:
-- - A machine (importer, algorithm, llm) never overwrites or deletes a row a person set;
--   norm.guard_people refuses it. Machines that disagree with a person report it instead.
-- - A write that changes nothing is not a change: writers update only when a value differs,
--   so re-running a bot logs nothing.
-- - Nothing a person said is lost: norm.changes keeps the before and after of every change.
--
-- People (lower priority) and events get tables in the same pattern when the work that fills
-- them lands; catalogue.md sketches them.

CREATE SCHEMA IF NOT EXISTS norm;

-- One batch of writes by one author: an import, a matcher or LLM run, a person's edit.
CREATE TABLE norm.runs (
    uuid        uuid PRIMARY KEY,
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,                  -- name@version (+ params hash or model), or the user uuid
    note        text,
    started_at  timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    tracks_rebuilt integer,                     -- tracks whose norm.track_songs rows this run wrote or cleared (derived rows aren't logged per row)
    UNIQUE (uuid, author_type, author_ref)      -- rows name their run with its author
);

-- A run is history: norm.changes names it, and replaying a change (a revert) needs it as it
-- was. It is never deleted, and only its finish (finished_at, tracks_rebuilt) is updated.
CREATE FUNCTION norm.runs_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR (NEW.uuid, NEW.author_type, NEW.author_ref, NEW.started_at)
                           IS DISTINCT FROM (OLD.uuid, OLD.author_type, OLD.author_ref, OLD.started_at) THEN
        RAISE EXCEPTION 'norm.runs rows are history; only finished_at, tracks_rebuilt and note change';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER runs_immutable BEFORE UPDATE OR DELETE ON norm.runs
    FOR EACH ROW EXECUTE FUNCTION norm.runs_immutable();

-- Provenance, on every catalogue row: author_type, author_ref, confidence (required for
-- machines, which are gated on it; people are 1 by convention), run_uuid, updated_at.

-- Songs are per artist (decided 2026-10-19): the same composition under two artists is two
-- songs. A merged song keeps its row and points at the survivor; readers follow merged_into.
CREATE TABLE norm.songs (
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
    run_uuid        uuid NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (artist_uuid, slug),
    UNIQUE (artist_uuid, uuid),                 -- target of per-artist references (aliases, merges)
    FOREIGN KEY (artist_uuid, merged_into) REFERENCES norm.songs (artist_uuid, uuid),
    CHECK (merged_into IS DISTINCT FROM uuid),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
CREATE INDEX ON norm.songs (artist_uuid);

-- The lookup every title match uses: a normalised spelling (TitleNormalizer) names one song
-- per artist. "Saturday Night", "One More Saturday Nite" and "OMSN" all point at one song.
CREATE TABLE norm.song_aliases (
    artist_uuid uuid NOT NULL,
    normalized  text NOT NULL,
    alias       text NOT NULL,                  -- as first seen
    song_uuid   uuid NOT NULL,
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid    uuid NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (artist_uuid, normalized),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref),
    -- an artist's spelling can only name that artist's song
    FOREIGN KEY (artist_uuid, song_uuid) REFERENCES norm.songs (artist_uuid, uuid)
);
CREATE INDEX ON norm.song_aliases (song_uuid);

-- Upstream songs (public.setlist_songs, from every importer; never edited) mapped onto one
-- catalogue song each.
CREATE TABLE norm.song_evidence (
    setlist_song_uuid uuid PRIMARY KEY,         -- public.setlist_songs.uuid; no FK: upstream rows get deleted
    song_uuid         uuid NOT NULL REFERENCES norm.songs (uuid),
    author_type       text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref        text NOT NULL,
    confidence        real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid          uuid NOT NULL,
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
CREATE INDEX ON norm.song_evidence (song_uuid);

-- What a track's title says it holds, one row per part in order (docs/matching.md T1).
-- song_uuid NULL = not a song (tuning, crowd) or not matched; text keeps the part's song name
-- as titled, notes what the title said about the recording ("aud -> sbd", "John Hartford
-- cover"). soundcheck: played at soundcheck, the same day but not part of the show.
-- basis says whether a row is a decision or follows from one (decided 2026-10-20):
-- - song_name, alias, noise, open: derived by the matcher from the artist's songs, aliases
--   and the title parse (open: a medley part nothing places yet, resolved = false). Rebuilt on every match, so fixing a song or an alias reaches every track;
--   not logged per row (norm.runs.tracks_rebuilt counts them).
-- - ruling: a decision about this track alone (the LLM on a one-off spelling, any person's
--   correction). Fill-only for machines, logged like every decision.
-- A medley whose parts are partly known keeps the known ones; the rest have resolved = false,
-- and later runs may fill them in without that counting as changing a settled fact.
CREATE TABLE norm.track_songs (
    track_uuid  uuid NOT NULL,                  -- public.source_tracks.uuid; no FK
    part        integer NOT NULL CHECK (part >= 1),
    song_uuid   uuid REFERENCES norm.songs (uuid),
    text        text,
    segue_after boolean NOT NULL DEFAULT false,
    resolved    boolean NOT NULL DEFAULT true,   -- false: a part nobody has placed yet; the track stays open
    qualifiers  text[] NOT NULL DEFAULT '{}'
                CHECK (qualifiers <@ ARRAY['reprise', 'partial', 'unfinished', 'instrumental', 'acoustic',
                                           'with_guest', 'continued', 'tape_flip', 'soundcheck',
                                           'tease', 'intro', 'outro', 'jam']),  -- jam: a jam on this song
    notes       text[] NOT NULL DEFAULT '{}',
    basis       text NOT NULL DEFAULT 'ruling' CHECK (basis IN ('song_name', 'alias', 'noise', 'open', 'ruling')),
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid    uuid NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (track_uuid, part),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    CHECK (author_type NOT IN ('user', 'moderator') OR basis = 'ruling'),  -- a person's row is a decision
    CHECK (basis <> 'open' OR (NOT resolved AND song_uuid IS NULL)),        -- an open part is unplaced
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
CREATE INDEX ON norm.track_songs (song_uuid) WHERE song_uuid IS NOT NULL;

-- Venues (decided 2026-10-23, v1 of normalization): a venue is a physical place, shared by
-- every artist who played it, normalized across the per-artist, per-upstream rows of
-- public.venues (216k rows over 4,364 artists). A room (a theater inside an arena, a festival
-- stage) has its site as parent: The Theater at Madison Square Garden is not the Garden.
-- name is the current official name; a show reads the name of its date (norm.venue_name_on).
CREATE TABLE norm.venues (
    uuid          uuid PRIMARY KEY,             -- uuid v7, minted in the app
    name          text NOT NULL,                -- today's official name
    slug          text NOT NULL UNIQUE,         -- display string, never an id
    kind          text NOT NULL DEFAULT 'site' CHECK (kind IN ('site', 'room')),
    parent_uuid   uuid REFERENCES norm.venues (uuid),  -- a room's site
    city          text,
    region        text,                         -- state or province, as its postal code where one exists ("PA", "QC")
    country       text CHECK (country ~ '^[A-Z]{2}$'),  -- ISO 3166-1 alpha-2
    latitude      double precision,
    longitude     double precision,
    coordinate_precision text CHECK (coordinate_precision IN ('venue', 'city')),
    merged_into   uuid REFERENCES norm.venues (uuid),  -- the surviving venue; readers follow it
    author_type   text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref    text NOT NULL,
    confidence    real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid      uuid NOT NULL,
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CHECK (parent_uuid IS NULL OR kind = 'room'),
    CHECK (parent_uuid IS DISTINCT FROM uuid AND merged_into IS DISTINCT FROM uuid),
    CHECK ((latitude IS NULL) = (longitude IS NULL) AND (latitude IS NULL) = (coordinate_precision IS NULL)),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);

-- A venue's names: official names over time (end exclusive: "Wachovia Center" to 2010-08-01,
-- "Wells Fargo Center" from it), short names ("MSG") and spellings seen in sources. A period
-- is one row, so a venue that returns to an earlier name has two rows of it (A → B → A); an
-- open start is '-infinity' (it is part of the key), an open end NULL. Matching looks a name up by its normalised form within the venue's locality, so
-- "Fox Theatre" in Oakland and in Boulder stay two venues.
CREATE TABLE norm.venue_names (
    venue_uuid  uuid NOT NULL REFERENCES norm.venues (uuid),
    normalized  text NOT NULL,                  -- VenueNormalizer.Key
    name        text NOT NULL,                  -- as written
    kind        text NOT NULL CHECK (kind IN ('official', 'short', 'spelling')),
    valid_from  date NOT NULL DEFAULT '-infinity',
    valid_to    date,
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid    uuid NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (venue_uuid, normalized, valid_from),
    CHECK (kind = 'official' OR (valid_from = '-infinity' AND valid_to IS NULL)),  -- only official names have dates
    CHECK (valid_to IS NULL OR valid_from < valid_to),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
CREATE INDEX ON norm.venue_names (normalized);

-- Ids other catalogues give the place: the strongest evidence that two rows are one venue
-- (16.8k legacy rows carry a setlist.fm or PhantasyTour venue id).
CREATE TABLE norm.venue_external_ids (
    provider    text NOT NULL CHECK (provider ~ '^[a-z]+$'),  -- setlistfm, phantasytour, musicbrainz, geonames, ...
    key         text NOT NULL,
    venue_uuid  uuid NOT NULL REFERENCES norm.venues (uuid),
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid    uuid NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (provider, key),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
CREATE INDEX ON norm.venue_external_ids (venue_uuid);

-- Each legacy venue row (one per artist and upstream; never edited) mapped onto its venue.
CREATE TABLE norm.venue_evidence (
    legacy_venue_uuid uuid PRIMARY KEY,         -- public.venues.uuid; no FK: upstream rows get deleted
    venue_uuid        uuid NOT NULL REFERENCES norm.venues (uuid),
    author_type       text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref        text NOT NULL,
    confidence        real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid          uuid NOT NULL,
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
CREATE INDEX ON norm.venue_evidence (venue_uuid);

-- Every show's venue: the new tables hold the whole picture, and a reader needs no legacy
-- table (decided 2026-10-23). basis says where it comes from:
-- - evidence: derived from norm.venue_evidence for the show's public.venues row; rebuilt by
--   the venue matcher on every run, not logged per row (like derived track rows).
-- - ruling: a decision about this show alone, where its legacy row is wrong or conflates two
--   places. Logged; fill-only for machines; a person's row is theirs.
CREATE TABLE norm.show_venues (
    show_uuid   uuid PRIMARY KEY,               -- public.shows.uuid; no FK
    venue_uuid  uuid NOT NULL REFERENCES norm.venues (uuid),
    basis       text NOT NULL DEFAULT 'ruling' CHECK (basis IN ('evidence', 'ruling')),
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),
    run_uuid    uuid NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (author_type NOT IN ('algorithm', 'llm') OR confidence IS NOT NULL),
    CHECK (author_type NOT IN ('user', 'moderator') OR basis = 'ruling'),  -- a person's row is a decision
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
CREATE INDEX ON norm.show_venues (venue_uuid);

-- The name a venue had on a date: its official name whose period covers the date, else today's.
CREATE FUNCTION norm.venue_name_on(venue uuid, on_date date) RETURNS text LANGUAGE sql STABLE AS $$
    -- Names stay on the rows of venues merged into this one: their periods count too, the
    -- venue's own first where periods overlap.
    WITH RECURSIVE merged AS (
        SELECT venue AS uuid, 0 AS depth
        UNION ALL
        SELECT m.uuid, f.depth + 1 FROM norm.venues m JOIN merged f ON m.merged_into = f.uuid
        WHERE f.depth < 20
    )
    SELECT coalesce(
        (SELECT n.name FROM norm.venue_names n JOIN merged f ON f.uuid = n.venue_uuid
         WHERE n.kind = 'official'
           AND n.valid_from <= on_date
           AND (n.valid_to IS NULL OR on_date < n.valid_to)
         ORDER BY f.depth, n.valid_from DESC LIMIT 1),
        (SELECT v.name FROM norm.venues v WHERE v.uuid = venue))
$$;

-- Open questions for a person (decided 2026-10-21): a title, track or song an expert or a run
-- couldn't settle. A run's still-unsure recurring titles land here with its proposal; an
-- expert marks items too ("needs a listen"). A match closes a machine's item once the title
-- resolves; a person's items stay until a person closes them. WS-7's review UI reads this.
CREATE TABLE norm.review_items (
    uuid        uuid PRIMARY KEY,               -- uuid v7, minted in the app
    subject     text NOT NULL DEFAULT 'song' CHECK (subject IN ('song', 'venue')),
    artist_uuid uuid,                           -- a song question's artist; venues are cross-artist
    key         text,                           -- the normalised title in question
    track_uuid  uuid,                           -- or one track (public.source_tracks.uuid; no FK)
    song_uuid   uuid REFERENCES norm.songs (uuid),  -- or one song
    venue_uuid  uuid REFERENCES norm.venues (uuid), -- or one venue
    -- What kind of question (decided 2026-10-24): 'title' (an unsure song title), 'track',
    -- 'spelling' (an unsure venue spelling), 'namesake' (one venue name in two towns of a
    -- state), 'same_town' (one name twice in a town), and kinds to come; no migration needed.
    question    text NOT NULL DEFAULT 'title' CHECK (question ~ '^[a-z_]+$'),
    refs        jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(refs) = 'object'),  -- its subjects: {"venues": [uuid, ...], "spelling": "..."}
    title       text NOT NULL,                  -- as people read it
    note        text,                           -- why it's open: "maybe Throwing Stones, needs a listen"
    proposal    text,                           -- a machine's answer below its bar ("→ Looks Like Rain")
    shows       integer,                        -- how many shows use the title
    resolved_at timestamptz,
    resolved_by text,                           -- author_ref of whoever closed it
    resolution  text,
    -- A person's answer, for code to apply: {"action": "merge", "into": uuid, "from": [uuid]},
    -- {"action": "two_places"}, {"action": "moved"}, {"action": "room_of", "room": uuid,
    -- "site": uuid}, {"action": "place", "venue": uuid}, {"action": "not_a_venue"}, ...
    answer      jsonb CHECK (answer IS NULL OR (jsonb_typeof(answer) = 'object' AND answer ? 'action')),
    author_type text NOT NULL CHECK (author_type IN ('importer', 'algorithm', 'llm', 'user', 'moderator')),
    author_ref  text NOT NULL,
    confidence  real CHECK (confidence BETWEEN 0 AND 1),  -- the proposal's
    run_uuid    uuid NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (key IS NOT NULL OR track_uuid IS NOT NULL OR song_uuid IS NOT NULL OR venue_uuid IS NOT NULL),
    CHECK (subject <> 'song' OR artist_uuid IS NOT NULL),
    CHECK ((resolved_at IS NULL) = (resolved_by IS NULL)),
    CHECK (answer IS NULL OR resolved_at IS NOT NULL),
    FOREIGN KEY (run_uuid, author_type, author_ref) REFERENCES norm.runs (uuid, author_type, author_ref)
);
-- One open item per title: per artist for songs, across artists for venues (two partial
-- indexes rather than NULLS NOT DISTINCT, which needs Postgres 15).
CREATE UNIQUE INDEX review_items_open_song_key ON norm.review_items (artist_uuid, key)
    WHERE subject = 'song' AND resolved_at IS NULL AND key IS NOT NULL;
CREATE UNIQUE INDEX review_items_open_venue_key ON norm.review_items (key)
    WHERE subject = 'venue' AND resolved_at IS NULL AND key IS NOT NULL;

-- Every insert, update and delete on a catalogue table, append-only. row_key is the row's
-- primary key. The author is the transaction's (SET LOCAL norm.author_type / norm.author_ref /
-- norm.run_uuid, which writers always set), else the new row's: a moderator reverting a run
-- is logged as the moderator, though the restored rows keep their original provenance.
CREATE TABLE norm.changes (
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
CREATE INDEX ON norm.changes (table_name, row_key);
CREATE INDEX ON norm.changes (run_uuid) WHERE run_uuid IS NOT NULL;

CREATE FUNCTION norm.changes_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'norm.changes is append-only';
END $$;
CREATE TRIGGER changes_immutable BEFORE UPDATE OR DELETE ON norm.changes
    FOR EACH ROW EXECUTE FUNCTION norm.changes_immutable();

-- Appends one norm.changes row. Trigger arguments name the primary key columns. A derived
-- track row (basis other than ruling, before and after) is not logged: it follows from rows
-- that are, and the matcher rebuilds it.
CREATE FUNCTION norm.log_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    old_row jsonb := CASE WHEN TG_OP IN ('UPDATE', 'DELETE') THEN to_jsonb(OLD) END;
    new_row jsonb := CASE WHEN TG_OP IN ('INSERT', 'UPDATE') THEN to_jsonb(NEW) END;
    key     jsonb;
BEGIN
    IF coalesce(old_row ->> 'basis', 'derived') <> 'ruling' AND coalesce(new_row ->> 'basis', 'derived') <> 'ruling'
       AND coalesce(old_row, new_row) ? 'basis' THEN
        RETURN NULL;
    END IF;
    SELECT jsonb_object_agg(k, coalesce(new_row, old_row) -> k) INTO key FROM unnest(TG_ARGV) AS k;
    -- A row keeps its key, so its history stays under one row_key: a new key is a delete
    -- and an insert.
    IF TG_OP = 'UPDATE' AND key IS DISTINCT FROM (SELECT jsonb_object_agg(k, old_row -> k) FROM unnest(TG_ARGV) AS k) THEN
        RAISE EXCEPTION '% keeps its primary key; delete and insert instead', TG_TABLE_NAME;
    END IF;
    INSERT INTO norm.changes (table_name, row_key, op, before, after, author_type, author_ref, run_uuid)
    VALUES (TG_TABLE_NAME, key, lower(TG_OP), old_row, new_row,
            coalesce(nullif(current_setting('norm.author_type', true), ''), new_row ->> 'author_type'),
            coalesce(nullif(current_setting('norm.author_ref', true), ''), new_row ->> 'author_ref'),
            coalesce(nullif(current_setting('norm.run_uuid', true), '')::uuid, (new_row ->> 'run_uuid')::uuid));
    RETURN NULL;
END $$;

-- People's rows are theirs: a machine may not overwrite or delete them. The actor is the
-- transaction's norm.author_type (a moderator restoring a machine's value is still a person);
-- an update or delete with no actor and run set is refused, never inferred from the row.
CREATE FUNCTION norm.guard_people() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    actor text := nullif(current_setting('norm.author_type', true), '');
BEGIN
    -- Every update and delete names its actor and a run of that actor, so the change log
    -- attributes it and a revert of the run finds it.
    IF NOT EXISTS (
           SELECT 1 FROM norm.runs r
           WHERE r.uuid = nullif(current_setting('norm.run_uuid', true), '')::uuid
             AND r.author_type = actor
             AND r.author_ref = nullif(current_setting('norm.author_ref', true), '')) THEN
        RAISE EXCEPTION 'changing % needs SET LOCAL norm.author_type, norm.author_ref and norm.run_uuid naming one run', TG_TABLE_NAME;
    END IF;
    IF OLD.author_type IN ('user', 'moderator') AND actor IS DISTINCT FROM 'user'
       AND actor IS DISTINCT FROM 'moderator' THEN
        RAISE EXCEPTION 'a % row was set by a person; % may not change it', TG_TABLE_NAME, coalesce(actor, 'an unnamed writer');
    END IF;
    -- A person's edit makes the row theirs, so no machine overwrites it later, whatever
    -- provenance the statement wrote. A revert (norm.restoring = on) restores the old
    -- provenance instead.
    IF TG_OP = 'UPDATE' AND actor IN ('user', 'moderator')
       AND coalesce(current_setting('norm.restoring', true), '') <> 'on' THEN
        NEW.author_type := actor;
        NEW.author_ref := current_setting('norm.author_ref', true);
        NEW.run_uuid := current_setting('norm.run_uuid', true)::uuid;
        IF TG_TABLE_NAME <> 'review_items' THEN  -- there it's the proposal's, not the author's
            NEW.confidence := NULL;
        END IF;
        IF TG_TABLE_NAME IN ('track_songs', 'show_venues') THEN
            NEW.basis := 'ruling';
        END IF;
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

-- Songs are per artist: a setlist song or a track maps only onto its own artist's song. The
-- upstream row is found through public.setlist_songs / public.source_tracks; one that is gone
-- (no FK, see above) can't be checked.
CREATE FUNCTION norm.same_artist() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    owner uuid;
BEGIN
    IF NEW.song_uuid IS NULL THEN
        RETURN NEW;
    END IF;
    IF TG_TABLE_NAME = 'song_evidence' THEN
        SELECT a.uuid INTO owner FROM public.setlist_songs ss JOIN public.artists a ON a.id = ss.artist_id
        WHERE ss.uuid = NEW.setlist_song_uuid;
    ELSE
        SELECT a.uuid INTO owner FROM public.source_tracks t JOIN public.artists a ON a.id = t.artist_id
        WHERE t.uuid = NEW.track_uuid;
    END IF;
    IF owner IS NOT NULL AND owner IS DISTINCT FROM (SELECT artist_uuid FROM norm.songs WHERE uuid = NEW.song_uuid) THEN
        RAISE EXCEPTION '% maps onto another artist''s song %', TG_TABLE_NAME, NEW.song_uuid;
    END IF;
    RETURN NEW;
END $$;

-- A song keeps its artist: its aliases, evidence and tracks were checked against it.
CREATE FUNCTION norm.songs_keep_artist() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.artist_uuid IS DISTINCT FROM OLD.artist_uuid THEN
        RAISE EXCEPTION 'a song keeps its artist; merge it into a song of the other artist''s catalogue instead';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER songs_keep_artist BEFORE UPDATE ON norm.songs
    FOR EACH ROW EXECUTE FUNCTION norm.songs_keep_artist();

-- A review item's song or track belongs to the item's artist.
CREATE FUNCTION norm.review_item_artist() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.song_uuid IS NOT NULL AND NOT EXISTS (
           SELECT 1 FROM norm.songs s WHERE s.uuid = NEW.song_uuid AND s.artist_uuid IS NOT DISTINCT FROM NEW.artist_uuid) THEN
        RAISE EXCEPTION 'review item names another artist''s song %', NEW.song_uuid;
    END IF;
    IF NEW.track_uuid IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM public.source_tracks t JOIN public.artists a ON a.id = t.artist_id
                   WHERE t.uuid = NEW.track_uuid AND a.uuid IS DISTINCT FROM NEW.artist_uuid) THEN
            RAISE EXCEPTION 'review item names another artist''s track %', NEW.track_uuid;
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER review_items_artist BEFORE INSERT OR UPDATE ON norm.review_items
    FOR EACH ROW EXECUTE FUNCTION norm.review_item_artist();

CREATE TRIGGER song_evidence_same_artist BEFORE INSERT OR UPDATE ON norm.song_evidence
    FOR EACH ROW EXECUTE FUNCTION norm.same_artist();
CREATE TRIGGER track_songs_same_artist BEFORE INSERT OR UPDATE ON norm.track_songs
    FOR EACH ROW EXECUTE FUNCTION norm.same_artist();

CREATE TRIGGER songs_guard BEFORE UPDATE OR DELETE ON norm.songs
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER songs_log AFTER INSERT OR UPDATE OR DELETE ON norm.songs
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('uuid');
CREATE TRIGGER song_aliases_guard BEFORE UPDATE OR DELETE ON norm.song_aliases
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER song_aliases_log AFTER INSERT OR UPDATE OR DELETE ON norm.song_aliases
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('artist_uuid', 'normalized');
CREATE TRIGGER song_evidence_guard BEFORE UPDATE OR DELETE ON norm.song_evidence
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER song_evidence_log AFTER INSERT OR UPDATE OR DELETE ON norm.song_evidence
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('setlist_song_uuid');
CREATE TRIGGER track_songs_guard BEFORE UPDATE OR DELETE ON norm.track_songs
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER track_songs_log AFTER INSERT OR UPDATE OR DELETE ON norm.track_songs
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('track_uuid', 'part');
CREATE TRIGGER venues_guard BEFORE UPDATE OR DELETE ON norm.venues
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER venues_log AFTER INSERT OR UPDATE OR DELETE ON norm.venues
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('uuid');
CREATE TRIGGER venue_names_guard BEFORE UPDATE OR DELETE ON norm.venue_names
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER venue_names_log AFTER INSERT OR UPDATE OR DELETE ON norm.venue_names
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('venue_uuid', 'normalized', 'valid_from');
CREATE TRIGGER venue_external_ids_guard BEFORE UPDATE OR DELETE ON norm.venue_external_ids
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER venue_external_ids_log AFTER INSERT OR UPDATE OR DELETE ON norm.venue_external_ids
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('provider', 'key');
CREATE TRIGGER venue_evidence_guard BEFORE UPDATE OR DELETE ON norm.venue_evidence
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER venue_evidence_log AFTER INSERT OR UPDATE OR DELETE ON norm.venue_evidence
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('legacy_venue_uuid');
CREATE TRIGGER show_venues_guard BEFORE UPDATE OR DELETE ON norm.show_venues
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER show_venues_log AFTER INSERT OR UPDATE OR DELETE ON norm.show_venues
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('show_uuid');
CREATE TRIGGER review_items_guard BEFORE UPDATE OR DELETE ON norm.review_items
    FOR EACH ROW EXECUTE FUNCTION norm.guard_people();
CREATE TRIGGER review_items_log AFTER INSERT OR UPDATE OR DELETE ON norm.review_items
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('uuid');
