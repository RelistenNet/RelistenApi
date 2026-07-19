namespace Relisten.Data
{
    internal static class CatalogReferenceSourceSetsSql
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
    source_set.*,
    source.uuid AS source_uuid,
    artist.uuid AS artist_uuid
FROM source_sets source_set
JOIN candidate_source_ids candidate ON candidate.id = source_set.source_id
JOIN sources source ON source.id = source_set.source_id
JOIN artists artist ON artist.id = source.artist_id
ORDER BY source_set.source_id, source_set.index;";
    }
}
