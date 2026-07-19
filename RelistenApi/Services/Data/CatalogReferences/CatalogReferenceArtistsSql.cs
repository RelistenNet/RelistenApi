namespace Relisten.Data
{
    internal static class CatalogReferenceArtistsSql
    {
        // Artists and features are separate result sets because Features is nested on the mobile DTO.
        // CatalogReferenceResolver attaches them after both sets have been read asynchronously.
        private const string CandidateArtistIds = CatalogReferenceSql.Requested + @",
candidate_artist_ids AS (
    SELECT entity.id
    FROM requested r
    JOIN artists entity ON r.catalog_type = 'artist' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT entity.artist_id
    FROM requested r
    JOIN shows entity ON r.catalog_type = 'show' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT entity.artist_id
    FROM requested r
    JOIN sources entity ON r.catalog_type = 'source' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT source.artist_id
    FROM requested r
    JOIN source_tracks entity ON r.catalog_type = 'source_track' AND entity.uuid = r.catalog_uuid
    JOIN sources source ON source.id = entity.source_id
    UNION
    SELECT entity.artist_id
    FROM requested r
    JOIN setlist_songs entity ON r.catalog_type = 'song' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT entity.artist_id
    FROM requested r
    JOIN tours entity ON r.catalog_type = 'tour' AND entity.uuid = r.catalog_uuid
    UNION
    SELECT entity.artist_id
    FROM requested r
    JOIN venues entity ON r.catalog_type = 'venue' AND entity.uuid = r.catalog_uuid
    WHERE entity.artist_id IS NOT NULL
)";

        public const string Query = CandidateArtistIds + @", show_counts AS (
    SELECT show_entity.artist_id, COUNT(*)::integer AS show_count
    FROM shows show_entity
    JOIN candidate_artist_ids candidate ON candidate.id = show_entity.artist_id
    GROUP BY show_entity.artist_id
), source_counts AS (
    SELECT info.artist_id, SUM(info.source_count)::integer AS source_count
    FROM show_source_information info
    JOIN candidate_artist_ids candidate ON candidate.id = info.artist_id
    GROUP BY info.artist_id
)
SELECT
    artist.*,
    COALESCE(show_counts.show_count, 0) AS show_count,
    COALESCE(source_counts.source_count, 0) AS source_count
FROM artists artist
JOIN candidate_artist_ids candidate ON candidate.id = artist.id
JOIN features feature ON feature.artist_id = artist.id
LEFT JOIN show_counts ON show_counts.artist_id = artist.id
LEFT JOIN source_counts ON source_counts.artist_id = artist.id
ORDER BY artist.uuid;

" + CandidateArtistIds + @"
SELECT feature.*
FROM features feature
JOIN candidate_artist_ids candidate ON candidate.id = feature.artist_id
ORDER BY feature.artist_id;";
    }
}
