-- Keep a single matcher implementation. NULL show scope preserves full-catalog
-- discovery/recounts; an empty scope returns no files. Explicit access paths avoid
-- a nullable OR hiding the show/source/track indexes in page-scoped reads.
CREATE FUNCTION norm.song_recording_matches(artist_uuid uuid,canonical_ids text[],show_uuids uuid[])
RETURNS TABLE (
    song_id text,track_uuid uuid,show_uuid uuid,matching_part_ordinals integer[],
    show_date date,source_uuid uuid,track_position integer
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
    ), matched_parts AS (
        SELECT m.canonical_id,t.uuid track_uuid,t.source_id,t.source_set_id,t.artist_id,t.track_position,ts.part
        FROM song_map m JOIN norm.track_songs ts ON ts.song_id=m.id AND ts.resolved
        JOIN public.source_tracks t ON t.uuid=ts.track_uuid AND NOT t.is_orphaned
        WHERE $3 IS NULL AND ($2 IS NULL OR m.canonical_id=ANY($2))
        UNION ALL
        SELECT m.canonical_id,t.uuid track_uuid,t.source_id,t.source_set_id,t.artist_id,t.track_position,ts.part
        FROM public.shows scope_show JOIN public.sources scope_source ON scope_source.show_id=scope_show.id
        JOIN public.source_tracks t ON t.source_id=scope_source.id AND NOT t.is_orphaned
        JOIN norm.track_songs ts ON ts.track_uuid=t.uuid AND ts.resolved
        JOIN song_map m ON m.id=ts.song_id
        WHERE $3 IS NOT NULL AND scope_show.uuid=ANY($3) AND ($2 IS NULL OR m.canonical_id=ANY($2))
    ), candidate_files AS MATERIALIZED (
        SELECT canonical_id,track_uuid,source_id,source_set_id,artist_id,track_position,
               array_agg(part ORDER BY part) matching_part_ordinals
        FROM matched_parts GROUP BY canonical_id,track_uuid,source_id,source_set_id,artist_id,track_position
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

-- Existing REST, ingestion/recount and administrative callers retain their entrypoint.
CREATE OR REPLACE FUNCTION norm.song_recording_matches(artist_uuid uuid,canonical_ids text[] DEFAULT NULL)
RETURNS TABLE (
    song_id text,track_uuid uuid,show_uuid uuid,matching_part_ordinals integer[],
    show_date date,source_uuid uuid,track_position integer
)
LANGUAGE sql STABLE SET search_path = pg_catalog, norm AS $$
    SELECT * FROM norm.song_recording_matches($1,$2,NULL::uuid[])
$$;
