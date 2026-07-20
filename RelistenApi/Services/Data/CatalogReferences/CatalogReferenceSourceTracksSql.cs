namespace Relisten.Data
{
    internal static class CatalogReferenceSourceTracksSql
    {
        public const string Query = CatalogReferenceSql.Requested + @"
SELECT
    track.*,
    source.uuid AS source_uuid,
    source_set.uuid AS source_set_uuid,
    artist.uuid AS artist_uuid,
    show_entity.uuid AS show_uuid
FROM requested r
JOIN source_tracks track ON r.catalog_type = 'source_track' AND track.uuid = r.catalog_uuid
JOIN sources source ON source.id = track.source_id
JOIN source_sets source_set
    ON source_set.id = track.source_set_id
    AND source_set.source_id = source.id
JOIN artists artist ON artist.id = source.artist_id
JOIN features feature ON feature.artist_id = artist.id
JOIN shows show_entity ON show_entity.id = source.show_id
WHERE track.is_orphaned = false
    AND (track.mp3_url IS NOT NULL OR track.flac_url IS NOT NULL)
ORDER BY track.uuid;";
    }
}
