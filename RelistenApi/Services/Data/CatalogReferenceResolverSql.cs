namespace Relisten.Data
{
    internal static class CatalogReferenceResolverSql
    {
        // Each result set is normalized and de-duplicated in PostgreSQL. Parent rows are included for
        // requested children so callers can render a favorite without issuing follow-up catalog reads.
        // A retained track without a media URL remains useful metadata, but its reference is unavailable;
        // this lets clients show a licensing removal in place instead of silently dropping the favorite.
        public const string Resolve = @"
WITH requested AS (
    SELECT catalog_type, catalog_uuid, ordinal
    FROM unnest(
        CAST(@catalogTypes AS text[]),
        CAST(@catalogUuids AS uuid[]),
        CAST(@ordinals AS integer[])
    ) AS requested(catalog_type, catalog_uuid, ordinal)
), available AS (
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN artists entity ON r.catalog_type = 'artist' AND entity.uuid = r.catalog_uuid
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN shows entity ON r.catalog_type = 'show' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN sources entity ON r.catalog_type = 'source' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN source_tracks entity ON r.catalog_type = 'source_track' AND entity.uuid = r.catalog_uuid
    JOIN sources source ON source.id = entity.source_id
    JOIN source_sets source_set ON source_set.id = entity.source_set_id
    JOIN artists artist ON artist.id = source.artist_id
    WHERE entity.mp3_url IS NOT NULL OR entity.flac_url IS NOT NULL
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN setlist_songs entity ON r.catalog_type = 'song' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN tours entity ON r.catalog_type = 'tour' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN venues entity ON r.catalog_type = 'venue' AND entity.uuid = r.catalog_uuid
)
SELECT
    r.catalog_type,
    r.catalog_uuid,
    CASE WHEN available.catalog_uuid IS NULL THEN 'unavailable' ELSE 'available' END AS availability
FROM requested r
LEFT JOIN available
    ON available.catalog_type = r.catalog_type
    AND available.catalog_uuid = r.catalog_uuid
ORDER BY r.ordinal;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
), candidate_artist_ids AS (
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
)
SELECT artist.uuid, artist.name, artist.slug
FROM artists artist
JOIN candidate_artist_ids candidate ON candidate.id = artist.id
ORDER BY artist.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
), candidate_show_ids AS (
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
    show_entity.uuid,
    artist.uuid AS artist_uuid,
    year_entity.uuid AS year_uuid,
    venue.uuid AS venue_uuid,
    tour.uuid AS tour_uuid,
    show_entity.date,
    show_entity.display_date
FROM shows show_entity
JOIN candidate_show_ids candidate ON candidate.id = show_entity.id
JOIN artists artist ON artist.id = show_entity.artist_id
LEFT JOIN years year_entity ON year_entity.id = show_entity.year_id
LEFT JOIN venues venue ON venue.id = show_entity.venue_id
LEFT JOIN tours tour ON tour.id = show_entity.tour_id
ORDER BY show_entity.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
), candidate_source_ids AS (
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
    source.uuid,
    artist.uuid AS artist_uuid,
    show_entity.uuid AS show_uuid,
    venue.uuid AS venue_uuid,
    source.display_date,
    source.is_soundboard,
    source.is_remaster
FROM sources source
JOIN candidate_source_ids candidate ON candidate.id = source.id
JOIN artists artist ON artist.id = source.artist_id
LEFT JOIN shows show_entity ON show_entity.id = source.show_id
LEFT JOIN venues venue ON venue.id = source.venue_id
ORDER BY source.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
)
SELECT
    track.uuid,
    source.uuid AS source_uuid,
    source_set.uuid AS source_set_uuid,
    artist.uuid AS artist_uuid,
    show_entity.uuid AS show_uuid,
    track.track_position,
    track.duration,
    track.title,
    track.mp3_url,
    track.flac_url
FROM requested r
JOIN source_tracks track ON r.catalog_type = 'source_track' AND track.uuid = r.catalog_uuid
JOIN sources source ON source.id = track.source_id
JOIN source_sets source_set ON source_set.id = track.source_set_id
JOIN artists artist ON artist.id = source.artist_id
LEFT JOIN shows show_entity ON show_entity.id = source.show_id
ORDER BY track.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
)
SELECT song.uuid, artist.uuid AS artist_uuid, song.name, song.slug
FROM requested r
JOIN setlist_songs song ON r.catalog_type = 'song' AND song.uuid = r.catalog_uuid
JOIN artists artist ON artist.id = song.artist_id
ORDER BY song.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
), candidate_tour_ids AS (
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
)
SELECT
    tour.uuid,
    artist.uuid AS artist_uuid,
    tour.name,
    tour.slug,
    tour.start_date,
    tour.end_date
FROM tours tour
JOIN candidate_tour_ids candidate ON candidate.id = tour.id
JOIN artists artist ON artist.id = tour.artist_id
ORDER BY tour.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
), candidate_venue_ids AS (
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
    venue.uuid,
    artist.uuid AS artist_uuid,
    COALESCE(venue.name, '') AS name,
    COALESCE(venue.location, '') AS location,
    COALESCE(venue.slug, '') AS slug
FROM venues venue
JOIN candidate_venue_ids candidate ON candidate.id = venue.id
LEFT JOIN artists artist ON artist.id = venue.artist_id
ORDER BY venue.uuid;
";
    }
}
