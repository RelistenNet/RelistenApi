namespace Relisten.Data
{
    internal static class CatalogReferenceToursSql
    {
        public const string Query = CatalogReferenceSql.Requested + @",
candidate_tour_ids AS (
    SELECT entity.id
    FROM requested r
    JOIN tours entity ON r.catalog_type = 'tour' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT tour.id
    FROM requested r
    JOIN shows show_entity ON r.catalog_type = 'show' AND show_entity.uuid = r.catalog_uuid
    JOIN tours tour ON tour.id = show_entity.tour_id
    UNION
    SELECT tour.id
    FROM requested r
    JOIN sources source ON r.catalog_type = 'source' AND source.uuid = r.catalog_uuid
    JOIN shows show_entity ON show_entity.id = source.show_id
    JOIN tours tour ON tour.id = show_entity.tour_id
    UNION
    SELECT tour.id
    FROM requested r
    JOIN source_tracks track ON r.catalog_type = 'source_track' AND track.uuid = r.catalog_uuid
    JOIN sources source ON source.id = track.source_id
    JOIN shows show_entity ON show_entity.id = source.show_id
    JOIN tours tour ON tour.id = show_entity.tour_id
), tour_show_counts AS (
    SELECT setlist_show.tour_id, COUNT(*) AS shows_on_tour
    FROM setlist_shows setlist_show
    JOIN candidate_tour_ids candidate ON candidate.id = setlist_show.tour_id
    GROUP BY setlist_show.tour_id
)
SELECT
    tour.*,
    artist.uuid AS artist_uuid,
    COALESCE(counts.shows_on_tour, 0)::integer AS shows_on_tour
FROM tours tour
JOIN candidate_tour_ids candidate ON candidate.id = tour.id
JOIN artists artist ON artist.id = tour.artist_id
JOIN features feature ON feature.artist_id = artist.id
LEFT JOIN tour_show_counts counts ON counts.tour_id = tour.id
WHERE tour.start_date IS NOT NULL AND tour.end_date IS NOT NULL
ORDER BY tour.uuid;";
    }
}
