-- Song pages read stored recording matches instead of running the matcher per request.
-- norm.song_recording_matches touches every file of a song on every tape (16k files for
-- Grateful Dead "Drums", ~365ms), and the page then ranks each show's tapes. The refresh
-- that already recounts songs after imports, normalization and admin edits now keeps this
-- copy, so song reads lag source writes exactly as long as song_counts already do.

-- The "best source first" order (see CatalogSourceRanking): soundboards, then Charlie
-- Miller / Peter Costello transfers, then the higher etree ID (the last all-digit dot
-- segment of the upstream identifier), then the higher weighted rating, then UUID. Every
-- field ascends so a plain ORDER BY on the composite ranks sources; record comparison
-- sorts a NULL field last, which puts a missing etree ID after every present one.
CREATE TYPE norm.source_preference AS (
    not_soundboard         boolean,
    not_preferred_transfer boolean,
    etree_id_descending    numeric,
    rating_descending      real,
    uuid                   uuid
);

-- A single expression without SET or sub-selects, so the planner inlines it into callers.
-- STABLE because concat() is; IMMUTABLE would block inlining.
CREATE FUNCTION norm.source_preference(src public.sources)
RETURNS norm.source_preference
LANGUAGE sql STABLE AS $$
    SELECT ROW(
        NOT src.is_soundboard,
        NOT concat(src.taper,src.transferrer,src.source) ~* '(charlie miller)|(peter costello)',
        -((regexp_match(src.upstream_identifier,'^(?:.*\.)?([0-9]+)(?:\.[^.]*[^0-9.][^.]*|\.)*$'))[1])::numeric,
        -COALESCE(src.avg_rating_weighted,0),
        src.uuid
    )::norm.source_preference
$$;

-- One row per file per surviving song, as norm.song_recording_matches returns them, plus
-- what song pages derive from each file. source_rank orders the show's tapes that contain
-- the song by norm.source_preference (1 = the tape a page plays). is_multi_song is NULL
-- while any part of the file is unresolved; duration is NULL unless positive.
CREATE TABLE norm.song_files (
    song_id                text NOT NULL REFERENCES norm.songs (id) ON DELETE CASCADE,
    track_uuid             uuid NOT NULL,
    artist_uuid            uuid NOT NULL REFERENCES public.artists (uuid) ON DELETE CASCADE,
    show_uuid              uuid NOT NULL,
    show_date              date NOT NULL,
    source_uuid            uuid NOT NULL,
    source_rank            integer NOT NULL CHECK (source_rank >= 1),
    track_position         integer NOT NULL,
    matching_part_ordinals integer[] NOT NULL,
    is_multi_song          boolean,
    duration               integer CHECK (duration > 0),
    PRIMARY KEY (song_id, track_uuid)
);
-- Song pages read a song's files in show order, best tape first.
CREATE INDEX song_files_song_order_idx
    ON norm.song_files (song_id, show_date, show_uuid, source_rank, track_position, track_uuid);
CREATE INDEX song_files_artist_idx ON norm.song_files (artist_uuid);

-- Runs the matcher once per artist: the files are stored first and the counts are derived
-- from them. Only rows that changed are written, so a refresh after a small import leaves
-- the rest of the artist's rows (and their WAL) alone.
CREATE OR REPLACE FUNCTION norm.refresh_song_counts(artist_uuid uuid)
RETURNS TABLE (songs_changed integer, imported_songs_changed integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, norm, pg_temp AS $$
DECLARE
    selected_artist uuid := artist_uuid;
    selected_artist_id integer;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('norm-catalogue'), hashtext(selected_artist::text));

    -- Read after the lock wait so a preceding moderator/normalizer commit is visible.
    SELECT a.id INTO selected_artist_id FROM public.artists a WHERE a.uuid=selected_artist;

    WITH matches AS MATERIALIZED (
        SELECT * FROM norm.song_recording_matches(selected_artist)
    ), preferences AS MATERIALIZED (
        -- A preference depends only on its source: evaluate it once per source, not per
        -- song and show (18k sources against 340k song/show/source rows for the Dead).
        SELECT src.uuid source_uuid,norm.source_preference(src) preference
        FROM (SELECT DISTINCT source_uuid FROM matches) m
        JOIN public.sources src ON src.uuid=m.source_uuid
    ), ranked AS (
        SELECT m.song_id,m.show_uuid,m.source_uuid,
               row_number() OVER (
                   PARTITION BY m.song_id,m.show_uuid ORDER BY p.preference
               )::integer source_rank
        FROM (SELECT DISTINCT song_id,show_uuid,source_uuid FROM matches) m
        JOIN preferences p USING (source_uuid)
    ), classified AS (
        -- A file also holds another song when a resolved part names a song outside the
        -- parts that matched this one. Unknown while any part is unresolved.
        SELECT m.song_id,m.track_uuid,
               CASE WHEN bool_or(p.resolved AND p.song_id IS NOT NULL
                                 AND NOT p.part=ANY(m.matching_part_ordinals)) THEN true
                    WHEN bool_and(p.resolved) THEN false END is_multi_song
        FROM matches m JOIN norm.track_songs p ON p.track_uuid=m.track_uuid
        GROUP BY m.song_id,m.track_uuid
    ), fresh AS MATERIALIZED (
        SELECT m.song_id,m.track_uuid,selected_artist artist_uuid,m.show_uuid,m.show_date,
               m.source_uuid,r.source_rank,m.track_position,m.matching_part_ordinals,
               c.is_multi_song,CASE WHEN t.duration>0 THEN t.duration END duration
        FROM matches m
        JOIN ranked r USING (song_id,show_uuid,source_uuid)
        JOIN classified c USING (song_id,track_uuid)
        JOIN public.source_tracks t ON t.uuid=m.track_uuid
    ), stored AS MATERIALIZED (
        SELECT * FROM norm.song_files f WHERE f.artist_uuid=selected_artist
    ), removed AS (
        DELETE FROM norm.song_files f
        USING stored s
        WHERE f.song_id=s.song_id AND f.track_uuid=s.track_uuid
          AND NOT EXISTS (
              SELECT 1 FROM fresh n WHERE n.song_id=s.song_id AND n.track_uuid=s.track_uuid
          )
    )
    -- Removed and written keys are disjoint, so the two writes never touch one row twice.
    INSERT INTO norm.song_files (
        song_id,track_uuid,artist_uuid,show_uuid,show_date,source_uuid,source_rank,
        track_position,matching_part_ordinals,is_multi_song,duration
    )
    SELECT n.song_id,n.track_uuid,n.artist_uuid,n.show_uuid,n.show_date,n.source_uuid,
           n.source_rank,n.track_position,n.matching_part_ordinals,n.is_multi_song,n.duration
    FROM fresh n LEFT JOIN stored s USING (song_id,track_uuid)
    WHERE s.song_id IS NULL
       OR (n.show_uuid,n.show_date,n.source_uuid,n.source_rank,n.track_position,
           n.matching_part_ordinals,n.is_multi_song,n.duration)
          IS DISTINCT FROM
          (s.show_uuid,s.show_date,s.source_uuid,s.source_rank,s.track_position,
           s.matching_part_ordinals,s.is_multi_song,s.duration)
    ON CONFLICT (song_id,track_uuid) DO UPDATE
    SET show_uuid=EXCLUDED.show_uuid,show_date=EXCLUDED.show_date,
        source_uuid=EXCLUDED.source_uuid,source_rank=EXCLUDED.source_rank,
        track_position=EXCLUDED.track_position,
        matching_part_ordinals=EXCLUDED.matching_part_ordinals,
        is_multi_song=EXCLUDED.is_multi_song,duration=EXCLUDED.duration;

    WITH counts AS MATERIALIZED (
        SELECT f.song_id,count(*)::integer matched_track_count,
               count(DISTINCT f.show_uuid)::integer matched_show_count
        FROM norm.song_files f
        WHERE f.artist_uuid=selected_artist
        GROUP BY f.song_id
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

-- Backfill inside the migration, like 016: each artist takes its catalogue lock until the
-- migration commits. Artists are ordered like 016 so concurrent lockers cannot deadlock.
DO $$
DECLARE
    artist uuid;
BEGIN
    FOR artist IN
        SELECT owners.artist_uuid FROM (SELECT DISTINCT s.artist_uuid FROM norm.songs s) owners
        ORDER BY hashtext(owners.artist_uuid::text),owners.artist_uuid
    LOOP
        PERFORM norm.refresh_song_counts(artist);
    END LOOP;
END $$;
