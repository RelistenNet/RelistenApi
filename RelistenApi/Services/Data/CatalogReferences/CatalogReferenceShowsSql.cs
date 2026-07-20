namespace Relisten.Data
{
    internal static class CatalogReferenceShowsSql
    {
        public const string Query = CatalogReferenceSql.Requested + @",
candidate_show_ids AS (
    SELECT entity.id
    FROM requested r
    JOIN shows entity ON r.catalog_type = 'show' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT show_entity.id
    FROM requested r
    JOIN sources source ON r.catalog_type = 'source' AND source.uuid = r.catalog_uuid
    JOIN shows show_entity ON show_entity.id = source.show_id
    UNION
    SELECT show_entity.id
    FROM requested r
    JOIN source_tracks track ON r.catalog_type = 'source_track' AND track.uuid = r.catalog_uuid
    JOIN sources source ON source.id = track.source_id
    JOIN shows show_entity ON show_entity.id = source.show_id
)
SELECT
    show_entity.*,
    artist.uuid AS artist_uuid,
    effective_year.uuid AS year_uuid,
    venue.uuid AS venue_uuid,
    tour.uuid AS tour_uuid,
    info.max_updated_at AS most_recent_source_updated_at,
    info.source_count::integer AS source_count,
    info.has_soundboard_source,
    info.has_flac AS has_streamable_flac_source
FROM shows show_entity
JOIN candidate_show_ids candidate ON candidate.id = show_entity.id
JOIN artists artist ON artist.id = show_entity.artist_id
JOIN features feature ON feature.artist_id = artist.id
JOIN show_source_information info ON info.show_id = show_entity.id
JOIN years effective_year
    ON effective_year.artist_id = show_entity.artist_id
    AND (
        effective_year.id = show_entity.year_id
        OR (
            show_entity.year_id IS NULL
            AND effective_year.year = EXTRACT(YEAR FROM show_entity.date)::integer::text
        )
    )
LEFT JOIN venues venue ON venue.id = show_entity.venue_id
LEFT JOIN tours tour ON tour.id = show_entity.tour_id
ORDER BY show_entity.uuid;";
    }
}
