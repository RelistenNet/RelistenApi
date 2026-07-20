namespace Relisten.Data
{
    internal static class CatalogReferenceYearsSql
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
), candidate_year_ids AS (
    SELECT effective_year.id
    FROM shows show_entity
    JOIN candidate_show_ids candidate ON candidate.id = show_entity.id
    JOIN years effective_year
        ON effective_year.artist_id = show_entity.artist_id
        AND (
            effective_year.id = show_entity.year_id
            OR (
                show_entity.year_id IS NULL
                AND effective_year.year = EXTRACT(YEAR FROM show_entity.date)::integer::text
            )
        )
)
SELECT year_entity.*, artist.uuid AS artist_uuid
FROM years year_entity
JOIN candidate_year_ids candidate ON candidate.id = year_entity.id
JOIN artists artist ON artist.id = year_entity.artist_id
ORDER BY year_entity.uuid;";
    }
}
