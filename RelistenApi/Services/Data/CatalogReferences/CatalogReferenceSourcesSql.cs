namespace Relisten.Data
{
    internal static class CatalogReferenceSourcesSql
    {
        public const string Query = CatalogReferenceSql.Requested + @",
candidate_source_ids AS (
    SELECT entity.id
    FROM requested r
    JOIN sources entity ON r.catalog_type = 'source' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT source.id
    FROM requested r
    JOIN source_tracks track ON r.catalog_type = 'source_track' AND track.uuid = r.catalog_uuid
    JOIN sources source ON source.id = track.source_id
)
SELECT
    source.*,
    artist.uuid AS artist_uuid,
    show_entity.uuid AS show_uuid,
    venue.uuid AS venue_uuid,
    COALESCE(review_counts.source_review_count, 0)::integer AS review_count
FROM sources source
JOIN candidate_source_ids candidate ON candidate.id = source.id
JOIN artists artist ON artist.id = source.artist_id
JOIN features feature ON feature.artist_id = artist.id
JOIN shows show_entity ON show_entity.id = source.show_id
LEFT JOIN venues venue ON venue.id = source.venue_id
LEFT JOIN source_review_counts review_counts ON review_counts.source_id = source.id
ORDER BY source.uuid;";
    }
}
