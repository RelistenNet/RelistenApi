-- Derived artist/place identities and eventually refreshed show counts. Import and
-- normalization activities run recounts after their source work commits. A periodic
-- full recount repairs admin/direct SQL changes and failed best-effort refreshes.
CREATE TABLE norm.artist_venues (
    id                text PRIMARY KEY DEFAULT norm.new_id('artist_venue')
                          CHECK (id ~ '^artist_venue_[0-9a-hjkmnp-tv-z]{8}$'),
    artist_uuid       uuid NOT NULL REFERENCES public.artists (uuid) ON DELETE CASCADE,
    venue_id          text NOT NULL REFERENCES norm.venues (id) ON DELETE CASCADE,
    artist_show_count integer NOT NULL DEFAULT 0 CHECK (artist_show_count >= 0),
    created_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (artist_uuid, venue_id)
);
CREATE INDEX artist_venues_venue_id ON norm.artist_venues (venue_id);

-- Keep zero-count pairs while their parents exist; deleting either parent removes them.
-- The refresh is the sole writer of derived counts and preserves IDs, pair keys, and
-- creation times. Only a changed count advances updated_at. Merge authority stays on
-- norm.venues; copying its redirects here would couple retirement to stale derived data.
CREATE FUNCTION norm.refresh_artist_venues(artist_filter uuid[] DEFAULT NULL)
RETURNS TABLE (artists_refreshed integer, relationships_changed integer)
LANGUAGE plpgsql AS $$
DECLARE
    selected_artists uuid[];
BEGIN
    -- Serialize recounts only. Source writers do not acquire this lock, and may commit
    -- new facts while a recount runs; their later hook or periodic reconciliation repairs
    -- any brief lag. A separate statement after this lock reads a fresh source snapshot.
    PERFORM pg_advisory_xact_lock(hashtext('norm-artist-venues'));

    -- NULL reconciles every live owner, including stored pairs whose shows disappeared.
    -- An explicit empty array does no work. Missing parents are never invented.
    IF artist_filter IS NULL THEN
        SELECT array_agg(artist_uuid ORDER BY artist_uuid) INTO selected_artists
        FROM (
            SELECT a.uuid AS artist_uuid
            FROM norm.show_venues sv JOIN public.shows s ON s.uuid = sv.show_uuid
            JOIN public.artists a ON a.id = s.artist_id
            UNION
            SELECT av.artist_uuid FROM norm.artist_venues av
        ) owners;
    ELSE
        SELECT array_agg(a.uuid ORDER BY a.uuid) INTO selected_artists
        FROM public.artists a WHERE a.uuid = ANY(artist_filter);
    END IF;
    IF selected_artists IS NULL THEN
        RETURN QUERY SELECT 0, 0;
        RETURN;
    END IF;

    -- Count each show at its canonical venue. Noncanonical pairs remain at zero, keeping
    -- their IDs available if a merge is later undone. A cyclic path has no survivor.
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
    ), inserted AS (
        INSERT INTO norm.artist_venues (artist_uuid, venue_id, artist_show_count)
        SELECT artist_uuid, venue_id, artist_show_count FROM counts
        ORDER BY artist_uuid, venue_id
        -- Only a pair conflict is benign. A random ID collision fails the refresh so its
        -- retry mints a fresh ID instead of overwriting another relationship.
        ON CONFLICT (artist_uuid, venue_id) DO NOTHING
        RETURNING id
    ), desired_counts AS (
        SELECT av.id, coalesce(c.artist_show_count, 0) AS artist_show_count
        FROM norm.artist_venues av
        LEFT JOIN counts c ON c.artist_uuid = av.artist_uuid AND c.venue_id = av.venue_id
        WHERE av.artist_uuid = ANY(selected_artists)
    ), updated AS (
        UPDATE norm.artist_venues av
        SET artist_show_count = d.artist_show_count, updated_at = clock_timestamp()
        FROM desired_counts d
        WHERE av.id = d.id
          AND av.artist_show_count <> d.artist_show_count
        RETURNING av.id
    )
    SELECT (SELECT count(*) FROM inserted) + (SELECT count(*) FROM updated)
    INTO relationships_changed;

    artists_refreshed := cardinality(selected_artists);
    RETURN NEXT;
END $$;

-- Existing admin roles keep only their source-table permissions. The application/migration
-- owner runs this derived maintenance function; no security-definer source hooks are needed.
REVOKE ALL ON FUNCTION norm.refresh_artist_venues(uuid[]) FROM PUBLIC;

-- Seed only the new derived table. No imported rows, normalization decisions, names, or
-- audit history change. Creation time is materialization time, not first performance date.
SELECT norm.refresh_artist_venues();
