BEGIN READ ONLY;
SET LOCAL statement_timeout='20s';
EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON)
            WITH requests AS (
              SELECT * FROM jsonb_to_recordset('[{"ordinal":0,"owner":"77a58ff9-2e01-c59c-b8eb-cff106049b72","owner_uuid":"77a58ff9-2e01-c59c-b8eb-cff106049b72","owner_kind":"Artist","artist":null,"artists":null,"date_from":null,"date_through":null,"venue":null,"song":null,"soundboard":null,"flac":null,"search":null,"show_uuid":null,"top_source_only":false,"month":null,"day":null,"recency":null,"recent_days":null,"require_sources":false,"include_auto":false,"include_fallback":false,"sort":"DateDescending","page_size":10,"has_after":false,"after_score":0,"after_secondary":0,"after_descending_label":"","after_label":"","after_id":"","need_count":true}]'::jsonb) AS r(
                ordinal int, owner text, owner_uuid uuid, owner_kind text, artist uuid, artists uuid[], date_from timestamp,
                date_through timestamp, venue text, song text, soundboard boolean, flac boolean,
                search text, include_auto boolean, include_fallback boolean, sort text, page_size int, has_after boolean,
                after_score float8, after_label text, after_id text, after_secondary float8, after_descending_label text, need_count boolean,
                month int,day int,recency text,recent_days int,require_sources boolean,show_uuid uuid,top_source_only boolean)
            )
            SELECT r.ordinal,p.* FROM requests r CROSS JOIN LATERAL (
              WITH candidates AS NOT MATERIALIZED (SELECT raw.key,raw.kind,raw.score,
  CASE WHEN r.search IS NULL AND r.sort='Default' THEN raw.label ELSE '' END label,
  0::float8 secondary,
  CASE WHEN r.search IS NOT NULL OR r.sort='DateDescending' THEN raw.label ELSE '' END descending_label
FROM (WITH song_matches AS MATERIALIZED (
SELECT song.id requested_song,m.* FROM norm.songs song JOIN artists owner_artist ON owner_artist.uuid=song.artist_uuid
JOIN features owner_feature ON owner_feature.artist_id=owner_artist.id AND owner_feature.norm_catalogue
CROSS JOIN LATERAL norm.song_recording_matches(song.artist_uuid,ARRAY[song.id]) m
WHERE song.id=ANY(ARRAY[r.song,CASE WHEN r.owner_kind='Song' THEN r.owner END])
)
SELECT s.uuid::text key,'Show' kind,
  CASE r.sort WHEN 'DateDescending' THEN 0
     WHEN 'Updated' THEN COALESCE(extract(epoch FROM CASE WHEN r.recency='Added' THEN cnt.max_updated_at ELSE s.updated_at END),-1e30)
     WHEN 'Added' THEN COALESCE(extract(epoch FROM cnt.max_created_at),0)
     WHEN 'Rating' THEN COALESCE(cnt.max_avg_rating_weighted,-1e30)
     WHEN 'Popularity' THEN COALESCE(p.hot_score_30d,0)
     ELSE CASE WHEN r.search IS NULL THEN 0 ELSE COALESCE(cnt.source_count,0) END END::float8 score,
  s.display_date label
FROM shows s JOIN artists a ON a.id=s.artist_id
LEFT JOIN features f ON f.artist_id=a.id
LEFT JOIN show_source_information cnt ON cnt.show_id=s.id
LEFT JOIN years y ON y.id=s.year_id LEFT JOIN tours t ON t.id=s.tour_id
LEFT JOIN show_popularity p ON p.uuid=s.uuid AND NOT p.is_global AND p.artist_uuid=a.uuid
WHERE (r.artist IS NULL OR a.uuid=r.artist)
  AND (r.artists IS NULL OR a.uuid=ANY(r.artists))
  AND (r.month IS NULL OR extract(month FROM s.date)=COALESCE(NULLIF(r.month,0),extract(month FROM CURRENT_DATE)))
  AND (r.day IS NULL OR extract(day FROM s.date)=COALESCE(NULLIF(r.day,0),extract(day FROM CURRENT_DATE)))
  AND (NOT r.require_sources OR cnt.show_id IS NOT NULL)
  AND (r.recency IS NULL
    OR r.recency='Performed' AND (r.recent_days IS NULL OR s.date>CURRENT_DATE-LEAST(r.recent_days,90)*INTERVAL '1 day')
    OR r.recency='Updated' AND (r.recent_days IS NULL OR s.updated_at>CURRENT_DATE-LEAST(r.recent_days,90)*INTERVAL '1 day')
    OR r.recency='Added' AND (CASE WHEN r.sort='Updated' THEN cnt.max_updated_at ELSE cnt.max_created_at END) IS NOT NULL
      AND (r.recent_days IS NULL OR (CASE WHEN r.sort='Updated' THEN cnt.max_updated_at ELSE cnt.max_created_at END)>=CURRENT_DATE-LEAST(r.recent_days,CURRENT_DATE-DATE '0001-01-01')*INTERVAL '1 day'))
  AND (r.date_from IS NULL OR s.date>=r.date_from)
  AND (r.date_through IS NULL OR s.date<=r.date_through)
  AND (r.soundboard IS NULL OR COALESCE(cnt.has_soundboard_source,false)=r.soundboard)
  AND (r.flac IS NULL OR COALESCE(cnt.has_flac,false)=r.flac)
  AND (r.search IS NULL OR s.display_date ILIKE r.search)
  AND (r.song IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (
      SELECT 1 FROM song_matches m WHERE m.requested_song=r.song AND m.show_uuid=s.uuid))
  AND (r.venue IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (
  WITH RECURSIVE forward AS (
    SELECT v.id,v.merged_into,ARRAY[v.id] path FROM norm.venues v WHERE v.id=r.venue
    UNION ALL
    SELECT v.id,v.merged_into,f.path || v.id FROM forward f JOIN norm.venues v ON v.id=f.merged_into
    WHERE cardinality(f.path)<32 AND NOT v.id=ANY(f.path)
  ), owners AS (
    SELECT id,ARRAY[id] path FROM forward WHERE merged_into IS NULL
    UNION ALL
    SELECT v.id,o.path || v.id FROM owners o JOIN norm.venues v ON v.merged_into=o.id
    WHERE cardinality(o.path)<32 AND NOT v.id=ANY(o.path)
  ) SELECT 1 FROM norm.show_venues sv JOIN owners o ON o.id=sv.venue_id WHERE sv.show_uuid=s.uuid
))
  AND (r.owner IS NULL
    OR r.owner_kind='Artist' AND a.uuid=r.owner_uuid
    OR r.owner_kind='Year' AND y.uuid=r.owner_uuid
    OR r.owner_kind='Tour' AND t.uuid=r.owner_uuid
    OR r.owner_kind='Song' AND COALESCE(f.norm_catalogue,false) AND EXISTS (
      SELECT 1 FROM song_matches m WHERE m.requested_song=r.owner AND m.show_uuid=s.uuid)
    OR r.owner_kind='Venue' AND COALESCE(f.norm_catalogue,false) AND EXISTS (
  WITH RECURSIVE forward AS (
    SELECT v.id,v.merged_into,ARRAY[v.id] path FROM norm.venues v WHERE v.id=r.owner
    UNION ALL
    SELECT v.id,v.merged_into,f.path || v.id FROM forward f JOIN norm.venues v ON v.id=f.merged_into
    WHERE cardinality(f.path)<32 AND NOT v.id=ANY(f.path)
  ), owners AS (
    SELECT id,ARRAY[id] path FROM forward WHERE merged_into IS NULL
    UNION ALL
    SELECT v.id,o.path || v.id FROM owners o JOIN norm.venues v ON v.merged_into=o.id
    WHERE cardinality(o.path)<32 AND NOT v.id=ANY(o.path)
  ) SELECT 1 FROM norm.show_venues sv JOIN owners o ON o.id=sv.venue_id WHERE sv.show_uuid=s.uuid
)
    OR r.owner_kind='ArtistVenue' AND COALESCE(f.norm_catalogue,false) AND EXISTS (
      SELECT 1 FROM norm.artist_venues av
      WHERE av.id=r.owner AND av.artist_uuid=a.uuid AND EXISTS (
  WITH RECURSIVE forward AS (
    SELECT v.id,v.merged_into,ARRAY[v.id] path FROM norm.venues v WHERE v.id=av.venue_id
    UNION ALL
    SELECT v.id,v.merged_into,f.path || v.id FROM forward f JOIN norm.venues v ON v.id=f.merged_into
    WHERE cardinality(f.path)<32 AND NOT v.id=ANY(f.path)
  ), owners AS (
    SELECT id,ARRAY[id] path FROM forward WHERE merged_into IS NULL
    UNION ALL
    SELECT v.id,o.path || v.id FROM owners o JOIN norm.venues v ON v.merged_into=o.id
    WHERE cardinality(o.path)<32 AND NOT v.id=ANY(o.path)
  ) SELECT 1 FROM norm.show_venues sv JOIN owners o ON o.id=sv.venue_id WHERE sv.show_uuid=s.uuid
))
    OR r.owner_kind='ImportedVenue' AND EXISTS (
      SELECT 1 FROM venues v WHERE v.id=s.venue_id AND v.uuid=r.owner_uuid)
    OR r.owner_kind='ImportedSong' AND EXISTS (
      SELECT 1 FROM setlist_songs song JOIN setlist_songs_plays sp ON sp.played_setlist_song_id=song.id
      JOIN setlist_shows ss ON ss.id=sp.played_setlist_show_id
      WHERE song.uuid=r.owner_uuid AND ss.artist_id=s.artist_id AND ss.date=s.date))) raw),
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
