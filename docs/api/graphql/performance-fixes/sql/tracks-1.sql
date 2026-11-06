BEGIN READ ONLY;
SET LOCAL statement_timeout='20s';
EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON)
            WITH requests AS (
              SELECT * FROM jsonb_to_recordset('[{"ordinal":0,"owner":"song_62mhvjyd","owner_uuid":null,"owner_kind":"Song","artist":null,"artists":null,"date_from":null,"date_through":null,"venue":null,"song":null,"soundboard":null,"flac":null,"search":null,"show_uuid":"8e9525c1-af00-2dcc-da1e-e8f172b73376","top_source_only":true,"month":null,"day":null,"recency":null,"recent_days":null,"require_sources":false,"include_auto":false,"include_fallback":false,"sort":"Default","page_size":20,"has_after":false,"after_score":0,"after_secondary":0,"after_descending_label":"","after_label":"","after_id":"","need_count":false}]'::jsonb) AS r(
                ordinal int, owner text, owner_uuid uuid, owner_kind text, artist uuid, artists uuid[], date_from timestamp,
                date_through timestamp, venue text, song text, soundboard boolean, flac boolean,
                search text, include_auto boolean, include_fallback boolean, sort text, page_size int, has_after boolean,
                after_score float8, after_label text, after_id text, after_secondary float8, after_descending_label text, need_count boolean,
                month int,day int,recency text,recent_days int,require_sources boolean,show_uuid uuid,top_source_only boolean)
            )
            , requested_songs AS MATERIALIZED (
  SELECT DISTINCT song.id,song.artist_uuid
  FROM requests r
  CROSS JOIN LATERAL unnest(ARRAY[r.song,CASE WHEN r.owner_kind='Song' THEN r.owner END]) requested(id)
  JOIN norm.songs song ON song.id=requested.id
  JOIN artists a ON a.uuid=song.artist_uuid
  JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
), song_matches AS MATERIALIZED (
  SELECT song.id requested_song,m.* FROM requested_songs song
  CROSS JOIN LATERAL norm.song_recording_matches(song.artist_uuid,ARRAY[song.id]) m
)
            SELECT r.ordinal,p.* FROM requests r CROSS JOIN LATERAL (
              WITH candidates AS NOT MATERIALIZED (SELECT raw.*,0::float8 secondary,''::text descending_label FROM (WITH ranked_sources AS MATERIALIZED (
  SELECT candidate.uuid,row_number() OVER (
    PARTITION BY candidate.show_id ORDER BY candidate.is_soundboard DESC,
concat(candidate.taper,candidate.transferrer,candidate.source) ~* '(charlie miller)|(peter costello)' DESC,
(SELECT segment::numeric FROM unnest(string_to_array(candidate.upstream_identifier,'.'))
     WITH ORDINALITY etree(segment,position)
 WHERE segment ~ '^[0-9]+$' ORDER BY position DESC LIMIT 1) DESC NULLS LAST,
COALESCE(candidate.avg_rating_weighted,0) DESC,
candidate.uuid
  ) source_rank
  FROM (
    SELECT DISTINCT m.source_uuid FROM song_matches m
    WHERE m.requested_song=COALESCE(CASE WHEN r.owner_kind='Song' THEN r.owner END,r.song)
      AND (r.show_uuid IS NULL OR m.show_uuid=r.show_uuid)
  ) matched
  JOIN sources candidate ON candidate.uuid=matched.source_uuid
  WHERE (r.owner_kind IS DISTINCT FROM 'Song' OR EXISTS(SELECT 1 FROM song_matches m WHERE m.requested_song=r.owner AND m.source_uuid=candidate.uuid))
    AND (r.song IS NULL OR EXISTS(SELECT 1 FROM song_matches m WHERE m.requested_song=r.song AND m.source_uuid=candidate.uuid))
    AND (r.soundboard IS NULL OR candidate.is_soundboard=r.soundboard)
    AND (r.flac IS NULL OR (candidate.flac_type IN (1,2))=r.flac)
)
SELECT t.uuid::text key,'Track' kind,0::float8 score,
  sh.display_date || ':' || sh.uuid || ':' ||
  CASE WHEN r.show_uuid IS NOT NULL THEN COALESCE(lpad(ranked.source_rank::text,12,'0'),src.uuid::text) ELSE src.uuid::text END
  || ':' || lpad(t.track_position::text,8,'0') label
FROM (SELECT DISTINCT m.track_uuid FROM song_matches m
 WHERE m.requested_song=COALESCE(CASE WHEN r.owner_kind='Song' THEN r.owner END,r.song)
   AND (r.show_uuid IS NULL OR m.show_uuid=r.show_uuid)) matched
JOIN source_tracks t ON t.uuid=matched.track_uuid
JOIN sources src ON src.id=t.source_id
JOIN source_sets ss ON ss.id=t.source_set_id AND ss.source_id=src.id
JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
LEFT JOIN features f ON f.artist_id=a.id
LEFT JOIN ranked_sources ranked ON ranked.uuid=src.uuid
WHERE NOT t.is_orphaned AND TRUE
  AND sh.uuid=r.show_uuid
  AND (NOT r.top_source_only OR ranked.source_rank=1)
  AND COALESCE(f.norm_catalogue,false) AND EXISTS (
  SELECT 1 FROM song_matches m WHERE m.requested_song=r.owner AND m.track_uuid=t.uuid)
  AND (r.song IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (
      SELECT 1 FROM song_matches m WHERE m.requested_song=r.song AND m.track_uuid=t.uuid))
  AND (r.date_from IS NULL OR sh.date>=r.date_from)
  AND (r.date_through IS NULL OR sh.date<=r.date_through)
  AND (r.venue IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (SELECT 1 FROM (  WITH RECURSIVE forward AS (
    SELECT v.id,v.merged_into,ARRAY[v.id] path FROM norm.venues v WHERE v.id=r.venue
    UNION ALL
    SELECT v.id,v.merged_into,f.path || v.id FROM forward f JOIN norm.venues v ON v.id=f.merged_into
    WHERE cardinality(f.path)<32 AND NOT v.id=ANY(f.path)
  ), owners AS (
    SELECT id,ARRAY[id] path FROM forward WHERE merged_into IS NULL
    UNION ALL
    SELECT v.id,o.path || v.id FROM owners o JOIN norm.venues v ON v.merged_into=o.id
    WHERE cardinality(o.path)<32 AND NOT v.id=ANY(o.path)
  ) SELECT sv.show_uuid FROM norm.show_venues sv JOIN owners o ON o.id=sv.venue_id) membership WHERE membership.show_uuid=sh.uuid))
  AND (r.search IS NULL OR t.title ILIKE r.search)
  AND (r.soundboard IS NULL OR src.is_soundboard=r.soundboard)
  AND (r.flac IS NULL OR (src.flac_type IN (1,2))=r.flac)) raw),
              page AS (
                SELECT * FROM candidates c WHERE NOT r.has_after OR c.score<r.after_score
                  OR (c.score=r.after_score AND c.secondary<r.after_secondary)
                  OR (c.score=r.after_score AND c.secondary=r.after_secondary AND c.descending_label COLLATE "C"<r.after_descending_label COLLATE "C")
                  OR (c.score=r.after_score AND c.secondary=r.after_secondary AND c.descending_label=r.after_descending_label
                      AND (c.label COLLATE "C",c.key COLLATE "C")>(r.after_label COLLATE "C",r.after_id COLLATE "C"))
                ORDER BY c.score DESC,c.secondary DESC,c.descending_label COLLATE "C" DESC,c.label COLLATE "C",c.key COLLATE "C"
                LIMIT r.page_size::bigint+1
              )
              SELECT page.*,
                     CASE WHEN r.need_count THEN (SELECT count(*) FROM candidates) END total
              FROM page
              UNION ALL
              SELECT NULL,NULL,NULL,NULL,NULL,NULL,
                     CASE WHEN r.need_count THEN (SELECT count(*) FROM candidates) END
              WHERE NOT EXISTS (SELECT 1 FROM page)
            ) p ORDER BY r.ordinal,p.score DESC,p.secondary DESC,p.descending_label COLLATE "C" DESC,p.label COLLATE "C",p.key COLLATE "C"
            ;
ROLLBACK;
