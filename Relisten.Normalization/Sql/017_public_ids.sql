-- Canonical rows may be physically removed by cleanup or a human revert. Keep their
-- public identities reserved so a later random mint cannot change a saved reference's
-- meaning. This table deliberately has no FK back to those removable rows.
CREATE TABLE norm.public_ids (
    id          text PRIMARY KEY,
    kind        text NOT NULL CHECK (kind IN ('song', 'venue')),
    last_name   text NOT NULL,
    retired_at  timestamptz,
    CHECK (id ~ ('^' || kind || '_[0-9a-hjkmnp-tv-z]{8}$'))
);

-- Close the backfill/trigger-install gap while retaining ordinary SELECT access.
LOCK TABLE norm.songs, norm.venues IN SHARE ROW EXCLUSIVE MODE;

INSERT INTO norm.public_ids (id, kind, last_name)
SELECT id, 'song', name FROM norm.songs
UNION ALL
SELECT id, 'venue', name FROM norm.venues;

-- Migration 14 also translated IDs retained only by history. Reserve known past
-- deletions without rewriting any historical row image. Active rows above win.
INSERT INTO norm.public_ids (id, kind, last_name, retired_at)
SELECT row_key ->> 'id',
       CASE table_name WHEN 'songs' THEN 'song' ELSE 'venue' END,
       before ->> 'name', at
FROM (
    SELECT DISTINCT ON (table_name, row_key) table_name, row_key, op, before, at
    FROM norm.changes WHERE table_name IN ('songs', 'venues')
    ORDER BY table_name, row_key, id DESC
) latest
WHERE op = 'delete'
ON CONFLICT (id) DO NOTHING;

CREATE FUNCTION norm.keep_public_id() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    entity_kind text := CASE TG_TABLE_NAME WHEN 'songs' THEN 'song' ELSE 'venue' END;
    actor text := nullif(current_setting('norm.author_type', true), '');
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE norm.public_ids
        SET retired_at = statement_timestamp(), last_name = OLD.name
        WHERE id = OLD.id;
    ELSE
        IF coalesce(current_setting('norm.restoring', true), '') = 'on' THEN
            IF actor NOT IN ('user', 'moderator') OR actor IS NULL OR NOT EXISTS (
                SELECT 1 FROM norm.runs r
                WHERE r.uuid = nullif(current_setting('norm.run_uuid', true), '')::uuid
                  AND r.author_type = actor
                  AND r.author_ref = nullif(current_setting('norm.author_ref', true), '')
            ) THEN
                RAISE EXCEPTION 'restoring a normalized public ID requires a human run'
                    USING ERRCODE = '42501';
            END IF;

            UPDATE norm.public_ids SET retired_at = NULL, last_name = NEW.name
            WHERE id = NEW.id AND kind = entity_kind AND retired_at IS NOT NULL;
            IF FOUND THEN
                RETURN NULL;
            END IF;
        END IF;

        INSERT INTO norm.public_ids (id, kind, last_name)
        VALUES (NEW.id, entity_kind, NEW.name);
    END IF;
    RETURN NULL;
END $$;

-- AFTER INSERT runs only for an actual insertion, so normal INSERT ... ON CONFLICT
-- updates retain their existing reservation. A reservation collision still rolls
-- back the complete statement. Both insertion and deletion acquire the canonical
-- row before its reservation, including when a remint waits for a concurrent delete.
-- Existing norm.log_change already refuses primary-key updates; ordinary updates
-- therefore need no additional reservation work.
CREATE TRIGGER songs_public_id AFTER INSERT OR DELETE ON norm.songs
    FOR EACH ROW EXECUTE FUNCTION norm.keep_public_id();
CREATE TRIGGER venues_public_id AFTER INSERT OR DELETE ON norm.venues
    FOR EACH ROW EXECUTE FUNCTION norm.keep_public_id();
