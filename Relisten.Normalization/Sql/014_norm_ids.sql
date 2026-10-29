-- RelistenNet/string-theory ddl/004_norm_ids.sql (2026-10-29), copied verbatim.
-- Venues and songs get public ids (decided 2026-10-29): a full-name prefix and 8 random
-- lowercase Crockford base32 characters, "venue_k3m9x2qa", "song_7hd2pw4e". The API serves
-- them, so they are short and stable. They are random because nothing could derive them: a
-- venue's name and slug change, and no upstream source identifies it. Runs, review items and
-- model calls keep their uuid v7; they are internal.
--
-- Applied in place to a populated catalogue: nothing is rebuilt or re-asked. Every stored
-- reference to a venue or song moves to its id, norm.changes and the receipts' outcomes
-- included, so history, blame and "revert run X" keep working across the change. Each venue
-- and song keeps its old uuid in retired_uuid (never served; NULL for rows minted after this).
--
-- Columns move: venues.uuid and songs.uuid -> id, parent_uuid -> parent_id, venue_uuid ->
-- venue_id, song_uuid -> song_id, all text. merged_into keeps its name.

-- 40 random bits: the first 10 hex digits of a uuid v4 are all random. Writers mint ids in
-- the app with the same alphabet; this one is for SQL (this migration, people's tools). A
-- freshly minted id is inserted without a conflict clause, so the primary key refuses the rare
-- collision instead of the write landing on the row that holds the id; the writer mints again.
CREATE FUNCTION norm.new_id(prefix text) RETURNS text LANGUAGE sql VOLATILE AS $$
    SELECT prefix || '_' || string_agg(
               substr('0123456789abcdefghjkmnpqrstvwxyz', ((r.bits >> (35 - 5 * i)) & 31)::int + 1, 1),
               '' ORDER BY i)
    FROM (SELECT ('x' || left(replace(gen_random_uuid()::text, '-', ''), 10))::bit(40)::bigint AS bits) r,
         generate_series(0, 7) AS i
$$;

-- The worker and the admin deploy with this migration, with the nightly schedule paused and
-- open venue and song runs drained: an older writer would fail against the renamed columns.
-- The locks only make any straggler wait for the move instead of interleaving with it.
LOCK TABLE norm.venues, norm.venue_names, norm.venue_external_ids, norm.venue_evidence,
    norm.show_venues, norm.songs, norm.song_aliases, norm.song_evidence, norm.track_songs,
    norm.review_items, norm.changes, norm.model_calls IN ACCESS EXCLUSIVE MODE;

-- The rewrite below is a translation, not an edit: no guard, no change log, and the
-- append-only tables accept it this once. Every trigger is back on at the end.
ALTER TABLE norm.venues DISABLE TRIGGER USER;
ALTER TABLE norm.venue_names DISABLE TRIGGER USER;
ALTER TABLE norm.venue_external_ids DISABLE TRIGGER USER;
ALTER TABLE norm.venue_evidence DISABLE TRIGGER USER;
ALTER TABLE norm.show_venues DISABLE TRIGGER USER;
ALTER TABLE norm.songs DISABLE TRIGGER USER;
ALTER TABLE norm.song_aliases DISABLE TRIGGER USER;
ALTER TABLE norm.song_evidence DISABLE TRIGGER USER;
ALTER TABLE norm.track_songs DISABLE TRIGGER USER;
ALTER TABLE norm.review_items DISABLE TRIGGER USER;
ALTER TABLE norm.changes DISABLE TRIGGER USER;
ALTER TABLE norm.model_calls DISABLE TRIGGER USER;

-- 1. Every venue and song's new id, minted until all are distinct. Those only the change
-- log remembers (a retired venue, a song a revert removed) get one too: a revert that brings
-- one back restores it under its id, with its old uuid in retired_uuid like the rest.
CREATE TEMP TABLE norm_id_owners ON COMMIT DROP AS
    SELECT uuid AS old, 'venue' AS prefix FROM norm.venues
    UNION SELECT uuid, 'song' FROM norm.songs
    UNION SELECT (row_key ->> 'uuid')::uuid, 'venue' FROM norm.changes WHERE table_name = 'venues'
    UNION SELECT (row_key ->> 'uuid')::uuid, 'song' FROM norm.changes WHERE table_name = 'songs';
CREATE TEMP TABLE norm_ids (old uuid PRIMARY KEY, new text NOT NULL UNIQUE) ON COMMIT DROP;
DO $$
BEGIN
    LOOP
        INSERT INTO norm_ids
        SELECT o.old, norm.new_id(o.prefix) FROM norm_id_owners o
        WHERE NOT EXISTS (SELECT 1 FROM norm_ids m WHERE m.old = o.old)
        ON CONFLICT DO NOTHING;
        EXIT WHEN NOT EXISTS (SELECT 1 FROM norm_id_owners o WHERE NOT EXISTS (SELECT 1 FROM norm_ids m WHERE m.old = o.old));
    END LOOP;
END $$;

-- 2. The references between rows. Foreign keys come off first; each table's id columns
-- change type together (a check compares them), take their ids, then their new names.
ALTER TABLE norm.venues DROP CONSTRAINT venues_parent_uuid_fkey, DROP CONSTRAINT venues_merged_into_fkey;
ALTER TABLE norm.venue_names DROP CONSTRAINT venue_names_venue_uuid_fkey;
ALTER TABLE norm.venue_external_ids DROP CONSTRAINT venue_external_ids_venue_uuid_fkey;
ALTER TABLE norm.venue_evidence DROP CONSTRAINT venue_evidence_venue_uuid_fkey;
ALTER TABLE norm.show_venues DROP CONSTRAINT show_venues_venue_uuid_fkey;
ALTER TABLE norm.songs DROP CONSTRAINT songs_artist_uuid_merged_into_fkey;
ALTER TABLE norm.song_aliases DROP CONSTRAINT song_aliases_artist_uuid_song_uuid_fkey;
ALTER TABLE norm.song_evidence DROP CONSTRAINT song_evidence_song_uuid_fkey;
ALTER TABLE norm.track_songs DROP CONSTRAINT track_songs_song_uuid_fkey;
ALTER TABLE norm.review_items DROP CONSTRAINT review_items_song_uuid_fkey, DROP CONSTRAINT review_items_venue_uuid_fkey;

ALTER TABLE norm.venues ADD COLUMN retired_uuid uuid UNIQUE;
UPDATE norm.venues SET retired_uuid = uuid;
ALTER TABLE norm.venues
    ALTER COLUMN uuid TYPE text USING uuid::text,
    ALTER COLUMN parent_uuid TYPE text USING parent_uuid::text,
    ALTER COLUMN merged_into TYPE text USING merged_into::text;
UPDATE norm.venues v SET
    uuid = (SELECT new FROM norm_ids WHERE old = v.uuid::uuid),
    parent_uuid = (SELECT new FROM norm_ids WHERE old = v.parent_uuid::uuid),
    merged_into = (SELECT new FROM norm_ids WHERE old = v.merged_into::uuid);
ALTER TABLE norm.venues RENAME COLUMN uuid TO id;
ALTER TABLE norm.venues RENAME COLUMN parent_uuid TO parent_id;

ALTER TABLE norm.songs ADD COLUMN retired_uuid uuid UNIQUE;
UPDATE norm.songs SET retired_uuid = uuid;
ALTER TABLE norm.songs
    ALTER COLUMN uuid TYPE text USING uuid::text,
    ALTER COLUMN merged_into TYPE text USING merged_into::text;
UPDATE norm.songs s SET
    uuid = (SELECT new FROM norm_ids WHERE old = s.uuid::uuid),
    merged_into = (SELECT new FROM norm_ids WHERE old = s.merged_into::uuid);
ALTER TABLE norm.songs RENAME COLUMN uuid TO id;
ALTER TABLE norm.songs RENAME CONSTRAINT songs_artist_uuid_uuid_key TO songs_artist_uuid_id_key;

ALTER TABLE norm.venue_names ALTER COLUMN venue_uuid TYPE text USING venue_uuid::text;
UPDATE norm.venue_names t SET venue_uuid = m.new FROM norm_ids m WHERE m.old = t.venue_uuid::uuid;
ALTER TABLE norm.venue_names RENAME COLUMN venue_uuid TO venue_id;

ALTER TABLE norm.venue_external_ids ALTER COLUMN venue_uuid TYPE text USING venue_uuid::text;
UPDATE norm.venue_external_ids t SET venue_uuid = m.new FROM norm_ids m WHERE m.old = t.venue_uuid::uuid;
ALTER TABLE norm.venue_external_ids RENAME COLUMN venue_uuid TO venue_id;
ALTER INDEX norm.venue_external_ids_venue_uuid_idx RENAME TO venue_external_ids_venue_id_idx;

ALTER TABLE norm.venue_evidence ALTER COLUMN venue_uuid TYPE text USING venue_uuid::text;
UPDATE norm.venue_evidence t SET venue_uuid = m.new FROM norm_ids m WHERE m.old = t.venue_uuid::uuid;
ALTER TABLE norm.venue_evidence RENAME COLUMN venue_uuid TO venue_id;
ALTER INDEX norm.venue_evidence_venue_uuid_idx RENAME TO venue_evidence_venue_id_idx;

ALTER TABLE norm.show_venues ALTER COLUMN venue_uuid TYPE text USING venue_uuid::text;
UPDATE norm.show_venues t SET venue_uuid = m.new FROM norm_ids m WHERE m.old = t.venue_uuid::uuid;
ALTER TABLE norm.show_venues RENAME COLUMN venue_uuid TO venue_id;
ALTER INDEX norm.show_venues_venue_uuid_idx RENAME TO show_venues_venue_id_idx;

ALTER TABLE norm.song_aliases ALTER COLUMN song_uuid TYPE text USING song_uuid::text;
UPDATE norm.song_aliases t SET song_uuid = m.new FROM norm_ids m WHERE m.old = t.song_uuid::uuid;
ALTER TABLE norm.song_aliases RENAME COLUMN song_uuid TO song_id;
ALTER INDEX norm.song_aliases_song_uuid_idx RENAME TO song_aliases_song_id_idx;

ALTER TABLE norm.song_evidence ALTER COLUMN song_uuid TYPE text USING song_uuid::text;
UPDATE norm.song_evidence t SET song_uuid = m.new FROM norm_ids m WHERE m.old = t.song_uuid::uuid;
ALTER TABLE norm.song_evidence RENAME COLUMN song_uuid TO song_id;
ALTER INDEX norm.song_evidence_song_uuid_idx RENAME TO song_evidence_song_id_idx;

ALTER TABLE norm.track_songs ALTER COLUMN song_uuid TYPE text USING song_uuid::text;
UPDATE norm.track_songs t SET song_uuid = m.new FROM norm_ids m WHERE m.old = t.song_uuid::uuid;
ALTER TABLE norm.track_songs RENAME COLUMN song_uuid TO song_id;
ALTER INDEX norm.track_songs_song_uuid_idx RENAME TO track_songs_song_id_idx;

ALTER TABLE norm.review_items
    ALTER COLUMN song_uuid TYPE text USING song_uuid::text,
    ALTER COLUMN venue_uuid TYPE text USING venue_uuid::text;
UPDATE norm.review_items t SET
    song_uuid = (SELECT new FROM norm_ids WHERE old = t.song_uuid::uuid),
    venue_uuid = (SELECT new FROM norm_ids WHERE old = t.venue_uuid::uuid)
WHERE song_uuid IS NOT NULL OR venue_uuid IS NOT NULL;
ALTER TABLE norm.review_items RENAME COLUMN song_uuid TO song_id;
ALTER TABLE norm.review_items RENAME COLUMN venue_uuid TO venue_id;

-- Ids are well formed, and the references come back.
ALTER TABLE norm.venues ADD CHECK (id ~ '^venue_[0-9a-hjkmnp-tv-z]{8}$');
ALTER TABLE norm.songs ADD CHECK (id ~ '^song_[0-9a-hjkmnp-tv-z]{8}$');
ALTER TABLE norm.venues
    ADD FOREIGN KEY (parent_id) REFERENCES norm.venues (id),
    ADD FOREIGN KEY (merged_into) REFERENCES norm.venues (id);
ALTER TABLE norm.venue_names ADD FOREIGN KEY (venue_id) REFERENCES norm.venues (id);
ALTER TABLE norm.venue_external_ids ADD FOREIGN KEY (venue_id) REFERENCES norm.venues (id);
ALTER TABLE norm.venue_evidence ADD FOREIGN KEY (venue_id) REFERENCES norm.venues (id);
ALTER TABLE norm.show_venues ADD FOREIGN KEY (venue_id) REFERENCES norm.venues (id);
ALTER TABLE norm.songs ADD FOREIGN KEY (artist_uuid, merged_into) REFERENCES norm.songs (artist_uuid, id);
ALTER TABLE norm.song_aliases ADD FOREIGN KEY (artist_uuid, song_id) REFERENCES norm.songs (artist_uuid, id);
ALTER TABLE norm.song_evidence ADD FOREIGN KEY (song_id) REFERENCES norm.songs (id);
ALTER TABLE norm.track_songs ADD FOREIGN KEY (song_id) REFERENCES norm.songs (id);
ALTER TABLE norm.review_items
    ADD FOREIGN KEY (song_id) REFERENCES norm.songs (id),
    ADD FOREIGN KEY (venue_id) REFERENCES norm.venues (id);

-- 3. Ids inside text and JSON. A venue or song's old uuid becomes its id wherever a string
-- holds it: a whole value (refs, answers) or part of one (a history question's key,
-- "history:<venue>"). Legacy uuids (review refs' "rows") and every other value stay.
CREATE FUNCTION pg_temp.translated_text(value text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE
    seen text;
    new_id text;
BEGIN
    FOR seen IN SELECT DISTINCT (regexp_matches(value, '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', 'g'))[1] LOOP
        SELECT m.new INTO new_id FROM norm_ids m WHERE m.old = seen::uuid;
        IF FOUND THEN
            value := replace(value, seen, new_id);
        END IF;
    END LOOP;
    RETURN value;
END $$;

CREATE FUNCTION pg_temp.translated(value jsonb) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN CASE jsonb_typeof(value)
        WHEN 'object' THEN (SELECT coalesce(jsonb_object_agg(k, pg_temp.translated(v)), '{}')
                            FROM jsonb_each(value) AS e(k, v))
        WHEN 'array' THEN (SELECT coalesce(jsonb_agg(pg_temp.translated(v) ORDER BY n), '[]')
                           FROM jsonb_array_elements(value) WITH ORDINALITY AS e(v, n))
        WHEN 'string' THEN to_jsonb(pg_temp.translated_text(value #>> '{}'))
        ELSE value
    END;
END $$;

-- A logged row image or key under the columns' new names.
CREATE FUNCTION pg_temp.renamed(table_name text, image jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
    SELECT jsonb_object_agg(
               CASE WHEN k = 'uuid' AND table_name IN ('venues', 'songs') THEN 'id'
                    WHEN k IN ('parent_uuid', 'venue_uuid', 'song_uuid') THEN replace(k, '_uuid', '_id')
                    ELSE k END,
               v)
    FROM jsonb_each(image) AS e(k, v)
$$;

UPDATE norm.review_items SET key = pg_temp.translated_text(key), refs = pg_temp.translated(refs),
    answer = pg_temp.translated(answer);

-- The change log's images match the rows as they are now, so a revert restores them and its
-- untouched check compares like with like: a venue or song image also carries retired_uuid.
UPDATE norm.changes SET
    row_key = pg_temp.translated(pg_temp.renamed(table_name, row_key)),
    before = pg_temp.translated(pg_temp.renamed(table_name, before))
             || CASE WHEN table_name IN ('venues', 'songs') THEN jsonb_build_object('retired_uuid', before -> 'uuid') ELSE '{}' END,
    after = pg_temp.translated(pg_temp.renamed(table_name, after))
            || CASE WHEN table_name IN ('venues', 'songs') THEN jsonb_build_object('retired_uuid', after -> 'uuid') ELSE '{}' END;

-- A receipt's outcome names the venues and songs a call wrote; the request and the reply
-- name none (they use the call's own refs) and stay exactly as sent and received.
UPDATE norm.model_calls SET outcome = pg_temp.translated(outcome);

DROP FUNCTION pg_temp.translated(jsonb);
DROP FUNCTION pg_temp.translated_text(text);
DROP FUNCTION pg_temp.renamed(text, jsonb);

-- 4. The functions and triggers that name the columns.
-- Roles granted the function outside this DDL (the admin's, sql/relisten_admin_roles.sql in
-- relisten-admin) keep it: a dropped function takes its grants with it.
CREATE TEMP TABLE venue_name_on_grants ON COMMIT DROP AS
    SELECT DISTINCT a.grantee::regrole::text AS role
    FROM pg_proc p, aclexplode(p.proacl) a
    WHERE p.oid = 'norm.venue_name_on(uuid, date)'::regprocedure
      AND a.privilege_type = 'EXECUTE' AND a.grantee <> 0 AND a.grantee <> p.proowner;
DROP FUNCTION norm.venue_name_on(uuid, date);
CREATE FUNCTION norm.venue_name_on(venue text, on_date date) RETURNS text LANGUAGE sql STABLE AS $$
    -- Names stay on the rows of venues merged into this one: their periods count too, the
    -- venue's own first where periods overlap.
    WITH RECURSIVE merged AS (
        SELECT venue AS id, 0 AS depth
        UNION ALL
        SELECT m.id, f.depth + 1 FROM norm.venues m JOIN merged f ON m.merged_into = f.id
        WHERE f.depth < 20
    )
    SELECT coalesce(
        (SELECT n.name FROM norm.venue_names n JOIN merged f ON f.id = n.venue_id
         WHERE n.kind = 'official'
           AND n.valid_from <= on_date
           AND (n.valid_to IS NULL OR on_date < n.valid_to)
         ORDER BY f.depth, n.valid_from DESC LIMIT 1),
        (SELECT v.name FROM norm.venues v WHERE v.id = venue))
$$;

DO $$
DECLARE
    grantee text;
BEGIN
    FOR grantee IN SELECT role FROM venue_name_on_grants LOOP
        EXECUTE format('GRANT EXECUTE ON FUNCTION norm.venue_name_on(text, date) TO %s', grantee);
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION norm.same_artist() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    owner uuid;
BEGIN
    IF NEW.song_id IS NULL THEN
        RETURN NEW;
    END IF;
    IF TG_TABLE_NAME = 'song_evidence' THEN
        SELECT a.uuid INTO owner FROM public.setlist_songs ss JOIN public.artists a ON a.id = ss.artist_id
        WHERE ss.uuid = NEW.setlist_song_uuid;
    ELSE
        SELECT a.uuid INTO owner FROM public.source_tracks t JOIN public.artists a ON a.id = t.artist_id
        WHERE t.uuid = NEW.track_uuid;
    END IF;
    IF owner IS NOT NULL AND owner IS DISTINCT FROM (SELECT artist_uuid FROM norm.songs WHERE id = NEW.song_id) THEN
        RAISE EXCEPTION '% maps onto another artist''s song %', TG_TABLE_NAME, NEW.song_id;
    END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION norm.review_item_artist() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.song_id IS NOT NULL AND NOT EXISTS (
           SELECT 1 FROM norm.songs s WHERE s.id = NEW.song_id AND s.artist_uuid IS NOT DISTINCT FROM NEW.artist_uuid) THEN
        RAISE EXCEPTION 'review item names another artist''s song %', NEW.song_id;
    END IF;
    IF NEW.track_uuid IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM public.source_tracks t JOIN public.artists a ON a.id = t.artist_id
                   WHERE t.uuid = NEW.track_uuid AND a.uuid IS DISTINCT FROM NEW.artist_uuid) THEN
            RAISE EXCEPTION 'review item names another artist''s track %', NEW.track_uuid;
        END IF;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER songs_log ON norm.songs;
CREATE TRIGGER songs_log AFTER INSERT OR UPDATE OR DELETE ON norm.songs
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('id');
DROP TRIGGER venues_log ON norm.venues;
CREATE TRIGGER venues_log AFTER INSERT OR UPDATE OR DELETE ON norm.venues
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('id');
DROP TRIGGER venue_names_log ON norm.venue_names;
CREATE TRIGGER venue_names_log AFTER INSERT OR UPDATE OR DELETE ON norm.venue_names
    FOR EACH ROW EXECUTE FUNCTION norm.log_change('venue_id', 'normalized', 'valid_from');

ALTER TABLE norm.venues ENABLE TRIGGER USER;
ALTER TABLE norm.venue_names ENABLE TRIGGER USER;
ALTER TABLE norm.venue_external_ids ENABLE TRIGGER USER;
ALTER TABLE norm.venue_evidence ENABLE TRIGGER USER;
ALTER TABLE norm.show_venues ENABLE TRIGGER USER;
ALTER TABLE norm.songs ENABLE TRIGGER USER;
ALTER TABLE norm.song_aliases ENABLE TRIGGER USER;
ALTER TABLE norm.song_evidence ENABLE TRIGGER USER;
ALTER TABLE norm.track_songs ENABLE TRIGGER USER;
ALTER TABLE norm.review_items ENABLE TRIGGER USER;
ALTER TABLE norm.changes ENABLE TRIGGER USER;
ALTER TABLE norm.model_calls ENABLE TRIGGER USER;
