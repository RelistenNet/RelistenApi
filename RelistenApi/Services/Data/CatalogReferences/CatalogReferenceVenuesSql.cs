namespace Relisten.Data
{
    internal static class CatalogReferenceVenuesSql
    {
        public const string Query = CatalogReferenceSql.Requested + @",
candidate_venue_ids AS (
    SELECT entity.id
    FROM requested r
    JOIN venues entity ON r.catalog_type = 'venue' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT venue.id
    FROM requested r
    JOIN shows show_entity ON r.catalog_type = 'show' AND show_entity.uuid = r.catalog_uuid
    JOIN venues venue ON venue.id = show_entity.venue_id
    UNION
    SELECT venue.id
    FROM requested r
    JOIN sources source ON r.catalog_type = 'source' AND source.uuid = r.catalog_uuid
    JOIN venues venue ON venue.id = source.venue_id
    UNION
    SELECT venue.id
    FROM requested r
    JOIN sources source ON r.catalog_type = 'source' AND source.uuid = r.catalog_uuid
    JOIN shows show_entity ON show_entity.id = source.show_id
    JOIN venues venue ON venue.id = show_entity.venue_id
    UNION
    SELECT venue.id
    FROM requested r
    JOIN source_tracks track ON r.catalog_type = 'source_track' AND track.uuid = r.catalog_uuid
    JOIN sources source ON source.id = track.source_id
    JOIN venues venue ON venue.id = source.venue_id
    UNION
    SELECT venue.id
    FROM requested r
    JOIN source_tracks track ON r.catalog_type = 'source_track' AND track.uuid = r.catalog_uuid
    JOIN sources source ON source.id = track.source_id
    JOIN shows show_entity ON show_entity.id = source.show_id
    JOIN venues venue ON venue.id = show_entity.venue_id
)
SELECT
    venue.*,
    artist.uuid AS artist_uuid,
    COALESCE(counts.shows_at_venue, 0)::integer AS shows_at_venue
FROM venues venue
JOIN candidate_venue_ids candidate ON candidate.id = venue.id
JOIN artists artist ON artist.id = venue.artist_id
JOIN features feature ON feature.artist_id = artist.id
LEFT JOIN venue_show_counts counts ON counts.id = venue.id
WHERE venue.name IS NOT NULL
    AND venue.location IS NOT NULL
    AND venue.upstream_identifier IS NOT NULL
    AND venue.slug IS NOT NULL
ORDER BY venue.uuid;";
    }
}
