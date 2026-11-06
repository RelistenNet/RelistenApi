namespace Relisten.Catalog;

/// <summary>Shared canonical/imported browse and search membership. These statements
/// run inside the collection reader's per-request lateral scope (alias r).</summary>
internal static class CatalogBrowseMembership
{
    internal static string Songs(CatalogCollection collection) =>
        $$"""
        WITH mappings AS ({{CatalogImportedSongMappingReader.CanonicalMappings(
            ArtistScope(collection))}})
        SELECT s.id key,'Song' kind,
          CASE WHEN r.search IS NULL THEN 0 ELSE COALESCE(counts.matched_show_count,0) END::float8 score,
          s.name label
        FROM norm.songs s JOIN artists a ON a.uuid=s.artist_uuid
        JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
        LEFT JOIN norm.song_counts counts ON counts.song_id=s.id
        WHERE s.merged_into IS NULL AND {{ArtistScope(collection)}}
          AND (r.search IS NULL OR EXISTS (
            WITH RECURSIVE lineage AS (
              SELECT s.id,s.name,ARRAY[s.id] path
              UNION ALL
              SELECT old.id,old.name,l.path || old.id FROM lineage l
              JOIN norm.songs old ON old.merged_into=l.id AND old.artist_uuid=s.artist_uuid
              WHERE cardinality(l.path)<32 AND NOT old.id=ANY(l.path)
            )
            SELECT 1 FROM lineage l LEFT JOIN norm.song_aliases alias
              ON alias.song_id=l.id AND alias.artist_uuid=s.artist_uuid
            WHERE l.name ILIKE r.search OR alias.alias ILIKE r.search
          ) OR EXISTS (
            SELECT 1 FROM mappings m JOIN setlist_songs raw ON raw.uuid=m.setlist_song_uuid
            WHERE m.song_id=s.id AND raw.name ILIKE r.search
          ))
        UNION ALL
        SELECT raw.uuid::text key,'ImportedSong' kind,
          CASE WHEN r.search IS NULL THEN 0 ELSE COALESCE(counts.setlist_show_count,0) END::float8 score,
          raw.name label
        FROM setlist_songs raw JOIN artists a ON a.id=raw.artist_id
        LEFT JOIN setlist_song_counts counts ON counts.setlist_song_id=raw.id
        WHERE r.include_fallback AND {{ArtistScope(collection)}}
          AND (r.search IS NULL OR raw.name ILIKE r.search)
          AND NOT EXISTS (SELECT 1 FROM mappings m WHERE m.setlist_song_uuid=raw.uuid)
        """;

    internal static string Venues(CatalogCollection collection) =>
        $$"""
            WITH RECURSIVE assignments AS (
              SELECT s.uuid show_uuid,s.venue_id imported_id,a.uuid artist_uuid,
                     CASE WHEN COALESCE(f.norm_catalogue,false) THEN sv.venue_id END venue_id
              FROM shows s JOIN artists a ON a.id=s.artist_id
              LEFT JOIN features f ON f.artist_id=a.id
              LEFT JOIN norm.show_venues sv ON sv.show_uuid=s.uuid
              WHERE {{ArtistScope(collection)}}
            ), chain AS (
              SELECT ass.show_uuid,ass.artist_uuid,ass.imported_id,v.id,v.merged_into,ARRAY[v.id] path
              FROM assignments ass JOIN norm.venues v ON v.id=ass.venue_id
              UNION ALL
              SELECT c.show_uuid,c.artist_uuid,c.imported_id,v.id,v.merged_into,c.path || v.id
              FROM chain c JOIN norm.venues v ON v.id=c.merged_into
              WHERE cardinality(c.path)<32 AND NOT v.id=ANY(c.path)
            ), resolved AS (
              SELECT ass.*,c.id canonical_id,av.id artist_venue_id
              FROM assignments ass LEFT JOIN chain c ON c.show_uuid=ass.show_uuid AND c.merged_into IS NULL
              LEFT JOIN norm.artist_venues av ON av.artist_uuid=ass.artist_uuid AND av.venue_id=c.id
            )
            SELECT av.id key,'ArtistVenue' kind,
              CASE WHEN r.search IS NULL THEN 0 ELSE av.artist_show_count END::float8 score,v.name label
            FROM norm.artist_venues av JOIN norm.venues v ON v.id=av.venue_id AND v.merged_into IS NULL
            JOIN artists a ON a.uuid=av.artist_uuid JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
            WHERE av.artist_show_count>0 AND {{ArtistScope(collection)}}
              AND (r.search IS NULL OR v.city ILIKE r.search OR v.region ILIKE r.search OR v.country ILIKE r.search
                OR EXISTS (
                  WITH RECURSIVE names AS (
                    SELECT v.id,v.name,ARRAY[v.id] path
                    UNION ALL
                    SELECT old.id,old.name,n.path || old.id FROM names n
                    JOIN norm.venues old ON old.merged_into=n.id
                    WHERE cardinality(n.path)<32 AND NOT old.id=ANY(n.path)
                  ) SELECT 1 FROM names n LEFT JOIN norm.venue_names alias ON alias.venue_id=n.id
                    WHERE n.name ILIKE r.search OR alias.name ILIKE r.search
                ) OR EXISTS (
                  SELECT 1 FROM resolved ass JOIN venues raw ON raw.id=ass.imported_id
                  WHERE ass.artist_venue_id=av.id
                    AND (raw.name ILIKE r.search OR raw.location ILIKE r.search OR raw.past_names ILIKE r.search)
                ))
            UNION ALL
            SELECT raw.uuid::text key,'ImportedVenue' kind,
              CASE WHEN r.search IS NULL THEN 0 ELSE (SELECT count(*) FROM shows s WHERE s.venue_id=raw.id) END::float8 score,
              COALESCE(raw.name,'') label
            FROM venues raw JOIN artists a ON a.id=raw.artist_id
            WHERE r.include_fallback AND {{ArtistScope(collection)}}
              AND (r.search IS NULL OR raw.name ILIKE r.search OR raw.location ILIKE r.search OR raw.past_names ILIKE r.search)
              AND (EXISTS (SELECT 1 FROM resolved ass WHERE ass.imported_id=raw.id AND ass.artist_venue_id IS NULL)
                OR r.search IS NOT NULL AND NOT EXISTS (SELECT 1 FROM shows s WHERE s.venue_id=raw.id))
            """;

    private static string ArtistScope(CatalogCollection collection)
    {
        var artist = collection.Filters?.Artist is not null ? "a.uuid=r.artist" : "TRUE";
        var owner = collection.Owner is not null ? "a.uuid=r.owner_uuid" : "TRUE";
        return $"{artist} AND {owner}";
    }
}
