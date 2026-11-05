-- Derived counters are separate from accepted normalization decisions and their audit log.
-- Counts may lag committed imports until their explicit refresh succeeds. Reads never recount.
CREATE TABLE norm.song_counts (
    song_id             text PRIMARY KEY REFERENCES norm.songs (id) ON DELETE CASCADE,
    matched_track_count integer NOT NULL DEFAULT 0 CHECK (matched_track_count >= 0),
    matched_show_count  integer NOT NULL DEFAULT 0 CHECK (matched_show_count >= 0),
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE public.setlist_song_counts (
    setlist_song_id     bigint PRIMARY KEY REFERENCES public.setlist_songs (id) ON DELETE CASCADE,
    setlist_show_count  integer NOT NULL DEFAULT 0 CHECK (setlist_show_count >= 0),
    created_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at         timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- One definition for discovery and recounts. A file occurs once for each surviving song,
-- even when several of its ordered parts match. Setlist evidence never establishes a file.
CREATE FUNCTION norm.song_recording_matches(artist_uuid uuid, canonical_ids text[] DEFAULT NULL)
RETURNS TABLE (
    song_id text,
    track_uuid uuid,
    show_uuid uuid,
    matching_part_ordinals integer[],
    show_date date,
    source_uuid uuid,
    track_position integer
)
LANGUAGE sql STABLE SET search_path = pg_catalog, norm AS $$
    -- Keep the complete artist map even for a single song: an invalid link in another
    -- resolved part makes the whole file interpretation unavailable. Reverse traversal
    -- from survivors excludes cycles and paths longer than the public 32-node limit.
    WITH RECURSIVE song_map AS (
        SELECT s.id,s.id canonical_id,ARRAY[s.id] path
        FROM norm.songs s
        WHERE s.artist_uuid=$1 AND s.merged_into IS NULL
        UNION ALL
        SELECT s.id,m.canonical_id,m.path || s.id
        FROM song_map m JOIN norm.songs s ON s.merged_into=m.id
        WHERE s.artist_uuid=$1 AND cardinality(m.path)<32
          AND NOT s.id=ANY(m.path)
    ), candidate_files AS MATERIALIZED (
        SELECT m.canonical_id,t.uuid track_uuid,t.source_id,t.source_set_id,
               t.artist_id,t.track_position,array_agg(ts.part ORDER BY ts.part) matching_part_ordinals
        FROM song_map m JOIN norm.track_songs ts ON ts.song_id=m.id AND ts.resolved
        JOIN public.source_tracks t ON t.uuid=ts.track_uuid AND NOT t.is_orphaned
        WHERE $2 IS NULL OR m.canonical_id=ANY($2)
        GROUP BY m.canonical_id,t.uuid,t.source_id,t.source_set_id,t.artist_id,t.track_position
    ), invalid_files AS MATERIALIZED (
        SELECT DISTINCT part.track_uuid
        FROM norm.track_songs part
        JOIN (SELECT DISTINCT track_uuid FROM candidate_files) c ON c.track_uuid=part.track_uuid
        LEFT JOIN song_map m ON m.id=part.song_id
        WHERE part.resolved AND part.song_id IS NOT NULL AND m.id IS NULL
    )
    SELECT c.canonical_id,c.track_uuid,sh.uuid,c.matching_part_ordinals,
           sh.date,src.uuid,c.track_position
    FROM candidate_files c
    JOIN public.artists a ON a.id=c.artist_id AND a.uuid=$1
    JOIN public.sources src ON src.id=c.source_id AND src.artist_id=c.artist_id
    JOIN public.source_sets sets ON sets.id=c.source_set_id AND sets.source_id=src.id
    JOIN public.shows sh ON sh.id=src.show_id AND sh.artist_id=c.artist_id
    LEFT JOIN invalid_files invalid ON invalid.track_uuid=c.track_uuid
    WHERE invalid.track_uuid IS NULL
$$;

-- Reuse the existing artist catalogue lock, already held by song normalization and admin
-- edits. No global lock is needed: an ordinary call owns exactly one artist. A retained
-- transaction that needs multiple artists must acquire its complete lock set in key order
-- before source writes. The C# service instead commits every artist separately.
CREATE FUNCTION norm.refresh_song_counts(artist_uuid uuid)
RETURNS TABLE (songs_changed integer, imported_songs_changed integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, norm AS $$
DECLARE
    selected_artist uuid := artist_uuid;
    selected_artist_id integer;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('norm-catalogue'), hashtext(selected_artist::text));

    -- Read after the lock wait so a preceding moderator/normalizer commit is visible.
    SELECT a.id INTO selected_artist_id FROM public.artists a WHERE a.uuid=selected_artist;

    WITH counts AS MATERIALIZED (
        SELECT m.song_id,count(*)::integer matched_track_count,
               count(DISTINCT m.show_uuid)::integer matched_show_count
        FROM norm.song_recording_matches(selected_artist) m
        GROUP BY m.song_id
    ), changed AS (
        INSERT INTO norm.song_counts (song_id,matched_track_count,matched_show_count)
        SELECT s.id,coalesce(c.matched_track_count,0),coalesce(c.matched_show_count,0)
        FROM norm.songs s LEFT JOIN counts c ON c.song_id=s.id
        WHERE s.artist_uuid=selected_artist
        ORDER BY s.id
        ON CONFLICT (song_id) DO UPDATE
        SET matched_track_count=EXCLUDED.matched_track_count,
            matched_show_count=EXCLUDED.matched_show_count,
            updated_at=clock_timestamp()
        WHERE (song_counts.matched_track_count,song_counts.matched_show_count)
            IS DISTINCT FROM (EXCLUDED.matched_track_count,EXCLUDED.matched_show_count)
        RETURNING song_id
    )
    SELECT count(*)::integer INTO songs_changed FROM changed;

    -- Aggregate the imported artist once. Multiple setlists can describe the same date;
    -- count distinct catalog show IDs, including distinct shows sharing that date.
    WITH counts AS MATERIALIZED (
        SELECT s.id,count(DISTINCT sh.id)::integer setlist_show_count
        FROM public.setlist_songs s
        JOIN public.setlist_songs_plays p ON p.played_setlist_song_id=s.id
        JOIN public.setlist_shows ss ON ss.id=p.played_setlist_show_id AND ss.artist_id=s.artist_id
        JOIN public.shows sh ON sh.artist_id=s.artist_id AND sh.date=ss.date
        WHERE s.artist_id=selected_artist_id
        GROUP BY s.id
    ), changed AS (
        INSERT INTO public.setlist_song_counts (setlist_song_id,setlist_show_count)
        SELECT s.id,coalesce(c.setlist_show_count,0)
        FROM public.setlist_songs s LEFT JOIN counts c ON c.id=s.id
        WHERE s.artist_id=selected_artist_id
        ORDER BY s.id
        ON CONFLICT (setlist_song_id) DO UPDATE
        SET setlist_show_count=EXCLUDED.setlist_show_count,updated_at=clock_timestamp()
        WHERE setlist_song_counts.setlist_show_count IS DISTINCT FROM EXCLUDED.setlist_show_count
        RETURNING setlist_song_id
    )
    SELECT count(*)::integer INTO imported_songs_changed FROM changed;

    RETURN NEXT;
END $$;

-- Admin edits invoke the owner-controlled recount in their existing transaction; they
-- receive no direct write permission on these counters. Later role provisioning must
-- grant the same narrow execution permission.
REVOKE ALL ON FUNCTION norm.refresh_song_counts(uuid) FROM PUBLIC;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='relisten_admin') THEN
        GRANT EXECUTE ON FUNCTION norm.refresh_song_counts(uuid) TO relisten_admin;
    END IF;
END $$;

-- Initial DDL and backfill share the migration transaction, so these one-time locks last
-- until that migration commits. Ordinary full repairs use SongCounts.RefreshAllAsync,
-- which commits each artist independently and never accumulates locks across artists.
DO $$
DECLARE
    artist uuid;
BEGIN
    FOR artist IN
        SELECT owners.artist_uuid FROM (
            SELECT s.artist_uuid FROM norm.songs s
            UNION
            SELECT a.uuid FROM public.setlist_songs s JOIN public.artists a ON a.id=s.artist_id
        ) owners
        ORDER BY hashtext(owners.artist_uuid::text),owners.artist_uuid
    LOOP
        PERFORM norm.refresh_song_counts(artist);
    END LOOP;
END $$;
