-- Stable artist/place identities and eventually refreshed show counts. These are derived
-- catalog rows, not normalization decisions: no author/run context is needed to refresh.
-- Source writers only mark artists dirty; they do not execute a recount. A separate short
-- refresh transaction owns the counters. Its queue-row lock can briefly delay another
-- request for the same artist, which is necessary to retain concurrent changes.
CREATE TABLE norm.artist_venues (
    id                text PRIMARY KEY DEFAULT norm.new_id('artist_venue')
                          CHECK (id ~ '^artist_venue_[0-9a-hjkmnp-tv-z]{8}$'),
    artist_uuid       uuid NOT NULL,
    venue_id          text NOT NULL CHECK (venue_id ~ '^venue_[0-9a-hjkmnp-tv-z]{8}$'),
    artist_show_count integer NOT NULL DEFAULT 0 CHECK (artist_show_count >= 0),
    created_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    merged_into       text,
    UNIQUE (artist_uuid, venue_id),
    UNIQUE (artist_uuid, id),
    FOREIGN KEY (artist_uuid, merged_into) REFERENCES norm.artist_venues (artist_uuid, id),
    CHECK (merged_into IS DISTINCT FROM id)
);
CREATE INDEX artist_venues_venue_id ON norm.artist_venues (venue_id);

-- Parent rows can disappear during catalog rebuild/retirement. Keep the relationship's
-- identity at zero instead of cascading it away or preventing the existing retirement.
-- A merged relationship redirects within the same artist; it never changes its pair key.
CREATE FUNCTION norm.artist_venues_keep_identity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.id, NEW.artist_uuid, NEW.venue_id, NEW.created_at)
       IS DISTINCT FROM (OLD.id, OLD.artist_uuid, OLD.venue_id, OLD.created_at) THEN
        RAISE EXCEPTION 'artist_venues keeps its identity and creation time';
    END IF;
    IF (NEW.artist_show_count, NEW.merged_into)
       IS DISTINCT FROM (OLD.artist_show_count, OLD.merged_into) THEN
        NEW.updated_at := clock_timestamp();
    ELSE
        NEW.updated_at := OLD.updated_at;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER artist_venues_keep_identity BEFORE UPDATE ON norm.artist_venues
    FOR EACH ROW EXECUTE FUNCTION norm.artist_venues_keep_identity();

CREATE TABLE norm.artist_venue_refresh_queue (
    artist_uuid  uuid PRIMARY KEY,
    requested_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- This helper is shared by source-table triggers and reconciliation. DO UPDATE is
-- intentional: it takes the queue-row lock. When a refresh has locked that row, a source
-- writer waits here, then recreates the request after the refresh commits its deletion.
-- DO NOTHING could discard that concurrent request and leave the stored count stale.
CREATE FUNCTION norm.request_artist_venue_refresh(artists uuid[] DEFAULT NULL)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, norm AS $$
BEGIN
    IF artists IS NULL THEN
        SELECT array_agg(DISTINCT id) INTO artists FROM (
            SELECT a.uuid AS id
            FROM norm.show_venues sv JOIN public.shows s ON s.uuid = sv.show_uuid
            JOIN public.artists a ON a.id = s.artist_id
            UNION
            SELECT av.artist_uuid FROM norm.artist_venues av
        ) known;
    END IF;

    INSERT INTO norm.artist_venue_refresh_queue (artist_uuid)
    SELECT DISTINCT id FROM unnest(artists) requested(id) WHERE id IS NOT NULL ORDER BY id
    ON CONFLICT (artist_uuid) DO UPDATE SET requested_at = clock_timestamp();
END $$;

-- One batch is atomic. Queue row locks serialize refreshes for an artist, but never
-- acquire locks on source rows. Other artists can refresh concurrently with SKIP LOCKED.
-- Source changes committed after this batch's snapshot retain a fresh pending request.
CREATE FUNCTION norm.refresh_artist_venues(
    artist_filter uuid[] DEFAULT NULL,
    batch_size integer DEFAULT 100
) RETURNS TABLE (artists_refreshed integer, relationships_changed integer)
LANGUAGE plpgsql AS $$
DECLARE
    selected_artists uuid[];
BEGIN
    IF batch_size IS NULL OR batch_size < 1 THEN
        RAISE EXCEPTION 'artist venue refresh batch size must be positive';
    END IF;

    SELECT array_agg(q.artist_uuid) INTO selected_artists
    FROM (
        SELECT pending.artist_uuid
        FROM norm.artist_venue_refresh_queue pending
        WHERE artist_filter IS NULL OR pending.artist_uuid = ANY(artist_filter)
        ORDER BY pending.requested_at, pending.artist_uuid
        LIMIT batch_size FOR UPDATE SKIP LOCKED
    ) q;
    IF selected_artists IS NULL THEN
        RETURN QUERY SELECT 0, 0;
        RETURN;
    END IF;

    -- Resolve existing identities too: a zero-count pair must still learn a later merge.
    -- Broken/deleted/cyclic venue paths have no survivor, so their old count becomes zero.
    WITH RECURSIVE chain AS (
        SELECT v.id AS requested_id, v.id, v.merged_into, ARRAY[v.id] AS path
        FROM norm.venues v
        UNION ALL
        SELECT c.requested_id, v.id, v.merged_into, c.path || v.id
        FROM chain c JOIN norm.venues v ON v.id = c.merged_into
        WHERE cardinality(c.path) < 32 AND NOT v.id = ANY(c.path)
    ), canonical AS MATERIALIZED (
        SELECT requested_id, id FROM chain WHERE merged_into IS NULL
    ), counts AS MATERIALIZED (
        SELECT a.uuid AS artist_uuid, c.id AS venue_id, count(*)::integer AS artist_show_count
        FROM norm.show_venues sv JOIN public.shows s ON s.uuid = sv.show_uuid
        JOIN public.artists a ON a.id = s.artist_id
        JOIN canonical c ON c.requested_id = sv.venue_id
        WHERE a.uuid = ANY(selected_artists)
        GROUP BY a.uuid, c.id
    ), desired_pairs AS MATERIALIZED (
        SELECT artist_uuid, venue_id FROM counts
        UNION
        SELECT av.artist_uuid, c.id
        FROM norm.artist_venues av JOIN canonical c ON c.requested_id = av.venue_id
        WHERE av.artist_uuid = ANY(selected_artists) AND c.id <> av.venue_id
    ), inserted AS (
        INSERT INTO norm.artist_venues (artist_uuid, venue_id, artist_show_count)
        SELECT p.artist_uuid, p.venue_id, coalesce(c.artist_show_count, 0)
        FROM desired_pairs p LEFT JOIN counts c USING (artist_uuid, venue_id)
        ORDER BY p.artist_uuid, p.venue_id
        -- Only a pair conflict is benign. A random ID collision fails the batch so its
        -- retry mints a fresh ID instead of overwriting another relationship.
        ON CONFLICT (artist_uuid, venue_id) DO NOTHING
        RETURNING id, artist_uuid, venue_id
    ), identities AS MATERIALIZED (
        SELECT id, artist_uuid, venue_id FROM norm.artist_venues
        WHERE artist_uuid = ANY(selected_artists)
        UNION ALL
        SELECT id, artist_uuid, venue_id FROM inserted
    ), desired_values AS (
        SELECT av.id, coalesce(c.artist_show_count, 0) AS artist_show_count,
               CASE WHEN target.id <> av.venue_id THEN survivor.id END AS merged_into
        FROM norm.artist_venues av
        LEFT JOIN counts c ON c.artist_uuid = av.artist_uuid AND c.venue_id = av.venue_id
        LEFT JOIN canonical target ON target.requested_id = av.venue_id
        LEFT JOIN identities survivor ON survivor.artist_uuid = av.artist_uuid AND survivor.venue_id = target.id
        WHERE av.artist_uuid = ANY(selected_artists)
    ), updated AS (
        UPDATE norm.artist_venues av
        SET artist_show_count = d.artist_show_count, merged_into = d.merged_into
        FROM desired_values d
        WHERE av.id = d.id
          AND (av.artist_show_count, av.merged_into) IS DISTINCT FROM (d.artist_show_count, d.merged_into)
        RETURNING av.id
    )
    SELECT (SELECT count(*) FROM inserted) + (SELECT count(*) FROM updated)
    INTO relationships_changed;

    DELETE FROM norm.artist_venue_refresh_queue WHERE artist_uuid = ANY(selected_artists);
    artists_refreshed := cardinality(selected_artists);
    RETURN NEXT;
END $$;

-- Statement-level transition tables collapse a bulk normalization/import write into one
-- request per artist. These SECURITY DEFINER triggers let the existing admin role write
-- source rows without granting it ownership of the derived queue/counters.
CREATE FUNCTION norm.queue_show_venue_artist_refresh() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, norm AS $$
DECLARE requested uuid[];
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT array_agg(DISTINCT a.uuid) INTO requested
        FROM new_rows n JOIN public.shows s ON s.uuid = n.show_uuid JOIN public.artists a ON a.id = s.artist_id;
    ELSIF TG_OP = 'DELETE' THEN
        SELECT array_agg(DISTINCT a.uuid) INTO requested
        FROM old_rows o JOIN public.shows s ON s.uuid = o.show_uuid JOIN public.artists a ON a.id = s.artist_id;
    ELSE
        SELECT array_agg(DISTINCT a.uuid) INTO requested
        FROM (
            SELECT coalesce(n.show_uuid, o.show_uuid) AS show_uuid
            FROM old_rows o FULL JOIN new_rows n ON n.show_uuid = o.show_uuid
            WHERE o.venue_id IS DISTINCT FROM n.venue_id
        ) changed JOIN public.shows s ON s.uuid = changed.show_uuid JOIN public.artists a ON a.id = s.artist_id;
    END IF;
    -- NULL is the public helper's explicit full-reconciliation request, not an empty batch.
    IF requested IS NOT NULL THEN PERFORM norm.request_artist_venue_refresh(requested); END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER artist_venues_show_links_insert AFTER INSERT ON norm.show_venues
    REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_show_venue_artist_refresh();
CREATE TRIGGER artist_venues_show_links_update AFTER UPDATE ON norm.show_venues
    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_show_venue_artist_refresh();
CREATE TRIGGER artist_venues_show_links_delete AFTER DELETE ON norm.show_venues
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_show_venue_artist_refresh();

CREATE FUNCTION norm.queue_catalog_show_artist_refresh() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, norm AS $$
DECLARE requested uuid[];
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT array_agg(DISTINCT a.uuid) INTO requested
        FROM new_rows n JOIN norm.show_venues sv ON sv.show_uuid = n.uuid JOIN public.artists a ON a.id = n.artist_id;
    ELSIF TG_OP = 'DELETE' THEN
        SELECT array_agg(DISTINCT a.uuid) INTO requested
        FROM old_rows o JOIN norm.show_venues sv ON sv.show_uuid = o.uuid JOIN public.artists a ON a.id = o.artist_id;
    ELSE
        SELECT array_agg(DISTINCT a.uuid) INTO requested
        FROM (
            SELECT o.artist_id, o.uuid FROM old_rows o FULL JOIN new_rows n USING (id)
            WHERE (o.artist_id, o.uuid) IS DISTINCT FROM (n.artist_id, n.uuid)
            UNION
            SELECT n.artist_id, n.uuid FROM old_rows o FULL JOIN new_rows n USING (id)
            WHERE (o.artist_id, o.uuid) IS DISTINCT FROM (n.artist_id, n.uuid)
        ) changed JOIN norm.show_venues sv ON sv.show_uuid = changed.uuid
        JOIN public.artists a ON a.id = changed.artist_id;
    END IF;
    IF requested IS NOT NULL THEN PERFORM norm.request_artist_venue_refresh(requested); END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER artist_venues_catalog_shows_insert AFTER INSERT ON public.shows
    REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_catalog_show_artist_refresh();
CREATE TRIGGER artist_venues_catalog_shows_update AFTER UPDATE ON public.shows
    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_catalog_show_artist_refresh();
CREATE TRIGGER artist_venues_catalog_shows_delete AFTER DELETE ON public.shows
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_catalog_show_artist_refresh();

CREATE FUNCTION norm.queue_catalog_artist_refresh() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, norm AS $$
DECLARE requested uuid[];
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT array_agg(uuid) INTO requested FROM old_rows;
    ELSE
        SELECT array_agg(DISTINCT id) INTO requested FROM (
            SELECT o.uuid AS id FROM old_rows o FULL JOIN new_rows n USING (id) WHERE o.uuid IS DISTINCT FROM n.uuid
            UNION
            SELECT n.uuid AS id FROM old_rows o FULL JOIN new_rows n USING (id) WHERE o.uuid IS DISTINCT FROM n.uuid
        ) changed WHERE id IS NOT NULL;
    END IF;
    IF requested IS NOT NULL THEN PERFORM norm.request_artist_venue_refresh(requested); END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER artist_venues_catalog_artists_update AFTER UPDATE ON public.artists
    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_catalog_artist_refresh();
CREATE TRIGGER artist_venues_catalog_artists_delete AFTER DELETE ON public.artists
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_catalog_artist_refresh();

CREATE FUNCTION norm.queue_merged_venue_artist_refresh() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, norm AS $$
DECLARE
    changed_ids text[];
    requested uuid[];
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT array_agg(id) INTO changed_ids FROM new_rows;
    ELSIF TG_OP = 'DELETE' THEN
        SELECT array_agg(id) INTO changed_ids FROM old_rows;
    ELSE
        SELECT array_agg(o.id) INTO changed_ids
        FROM old_rows o JOIN new_rows n USING (id) WHERE o.merged_into IS DISTINCT FROM n.merged_into;
    END IF;
    IF changed_ids IS NULL THEN RETURN NULL; END IF;

    WITH RECURSIVE affected AS (
        SELECT id, ARRAY[id] AS path FROM unnest(changed_ids) changed(id)
        UNION ALL
        SELECT v.id, p.path || v.id FROM affected p JOIN norm.venues v ON v.merged_into = p.id
        WHERE cardinality(p.path) < 32 AND NOT v.id = ANY(p.path)
    )
    SELECT array_agg(DISTINCT artist_uuid) INTO requested FROM (
        SELECT a.uuid AS artist_uuid
        FROM affected p JOIN norm.show_venues sv ON sv.venue_id = p.id
        JOIN public.shows s ON s.uuid = sv.show_uuid JOIN public.artists a ON a.id = s.artist_id
        UNION
        SELECT av.artist_uuid FROM affected p JOIN norm.artist_venues av ON av.venue_id = p.id
    ) artists;
    IF requested IS NOT NULL THEN PERFORM norm.request_artist_venue_refresh(requested); END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER artist_venues_venue_merge AFTER UPDATE ON norm.venues
    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_merged_venue_artist_refresh();
CREATE TRIGGER artist_venues_venue_restore AFTER INSERT ON norm.venues
    REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_merged_venue_artist_refresh();
CREATE TRIGGER artist_venues_venue_retirement AFTER DELETE ON norm.venues
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION norm.queue_merged_venue_artist_refresh();

-- The application/migration owner drains the queue. Existing admin roles only need their
-- source-table permissions: trigger execution uses the owner's enqueue privileges.
REVOKE ALL ON FUNCTION norm.request_artist_venue_refresh(uuid[]) FROM PUBLIC;
REVOKE ALL ON FUNCTION norm.refresh_artist_venues(uuid[], integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION norm.queue_show_venue_artist_refresh() FROM PUBLIC;
REVOKE ALL ON FUNCTION norm.queue_catalog_show_artist_refresh() FROM PUBLIC;
REVOKE ALL ON FUNCTION norm.queue_catalog_artist_refresh() FROM PUBLIC;
REVOKE ALL ON FUNCTION norm.queue_merged_venue_artist_refresh() FROM PUBLIC;

-- Seed only this new derived table. No venue/song decisions, imported rows or audit
-- history are changed. Refresh all existing pairs before the migration becomes visible.
SELECT norm.request_artist_venue_refresh();
DO $$
DECLARE refreshed integer;
BEGIN
    LOOP
        SELECT artists_refreshed INTO refreshed FROM norm.refresh_artist_venues();
        EXIT WHEN refreshed = 0;
    END LOOP;
END $$;
