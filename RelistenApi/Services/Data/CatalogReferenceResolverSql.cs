namespace Relisten.Data
{
    internal static class CatalogReferenceResolverSql
    {
        // Result sets use the same complete projections as the ordinary v3 endpoints. Parent rows are
        // normalized so a fresh client can hydrate Realm without follow-up requests or invented values.
        public const string Resolve = @"
WITH requested AS (
    SELECT catalog_type, catalog_uuid, ordinal
    FROM unnest(
        CAST(@catalogTypes AS text[]),
        CAST(@catalogUuids AS uuid[])
    ) WITH ORDINALITY AS requested(catalog_type, catalog_uuid, ordinal)
), available AS (
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN artists entity ON r.catalog_type = 'artist' AND entity.uuid = r.catalog_uuid
    JOIN features feature ON feature.artist_id = entity.id
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN shows entity ON r.catalog_type = 'show' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    JOIN features feature ON feature.artist_id = artist.id
    JOIN show_source_information info ON info.show_id = entity.id
    JOIN years effective_year
        ON effective_year.artist_id = entity.artist_id
        AND (
            effective_year.id = entity.year_id
            OR (
                entity.year_id IS NULL
                AND effective_year.year = EXTRACT(YEAR FROM entity.date)::integer::text
            )
        )
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN sources entity ON r.catalog_type = 'source' AND entity.uuid = r.catalog_uuid
    JOIN shows show_entity ON show_entity.id = entity.show_id
    JOIN artists show_artist ON show_artist.id = show_entity.artist_id
    JOIN features show_feature ON show_feature.artist_id = show_artist.id
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
    JOIN artists source_artist ON source_artist.id = entity.artist_id
    JOIN features source_feature ON source_feature.artist_id = source_artist.id
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN source_tracks entity ON r.catalog_type = 'source_track' AND entity.uuid = r.catalog_uuid
    JOIN sources source ON source.id = entity.source_id
    JOIN source_sets source_set
        ON source_set.id = entity.source_set_id
        AND source_set.source_id = entity.source_id
    JOIN shows show_entity ON show_entity.id = source.show_id
    JOIN artists show_artist ON show_artist.id = show_entity.artist_id
    JOIN features show_feature ON show_feature.artist_id = show_artist.id
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
    JOIN artists source_artist ON source_artist.id = source.artist_id
    JOIN features source_feature ON source_feature.artist_id = source_artist.id
    WHERE entity.is_orphaned = false
        AND (entity.mp3_url IS NOT NULL OR entity.flac_url IS NOT NULL)
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN setlist_songs entity ON r.catalog_type = 'song' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    JOIN features feature ON feature.artist_id = artist.id
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN tours entity ON r.catalog_type = 'tour' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    JOIN features feature ON feature.artist_id = artist.id
    WHERE entity.start_date IS NOT NULL AND entity.end_date IS NOT NULL
    UNION ALL
    SELECT r.catalog_type, r.catalog_uuid
    FROM requested r
    JOIN venues entity ON r.catalog_type = 'venue' AND entity.uuid = r.catalog_uuid
    JOIN artists artist ON artist.id = entity.artist_id
    JOIN features feature ON feature.artist_id = artist.id
    WHERE entity.name IS NOT NULL
        AND entity.location IS NOT NULL
        AND entity.upstream_identifier IS NOT NULL
        AND entity.slug IS NOT NULL
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
), show_counts AS (
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

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
), candidate_artist_ids AS (
    SELECT entity.id FROM requested r
    JOIN artists entity ON r.catalog_type = 'artist' AND entity.uuid = r.catalog_uuid
    UNION SELECT entity.artist_id FROM requested r
    JOIN shows entity ON r.catalog_type = 'show' AND entity.uuid = r.catalog_uuid
    UNION SELECT entity.artist_id FROM requested r
    JOIN sources entity ON r.catalog_type = 'source' AND entity.uuid = r.catalog_uuid
    UNION SELECT source.artist_id FROM requested r
    JOIN source_tracks entity ON r.catalog_type = 'source_track' AND entity.uuid = r.catalog_uuid
    JOIN sources source ON source.id = entity.source_id
    UNION SELECT entity.artist_id FROM requested r
    JOIN setlist_songs entity ON r.catalog_type = 'song' AND entity.uuid = r.catalog_uuid
    UNION SELECT entity.artist_id FROM requested r
    JOIN tours entity ON r.catalog_type = 'tour' AND entity.uuid = r.catalog_uuid
    UNION SELECT entity.artist_id FROM requested r
    JOIN venues entity ON r.catalog_type = 'venue' AND entity.uuid = r.catalog_uuid
    WHERE entity.artist_id IS NOT NULL
)
SELECT feature.*
FROM features feature
JOIN candidate_artist_ids candidate ON candidate.id = feature.artist_id
ORDER BY feature.artist_id;

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
ORDER BY source.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
)
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
ORDER BY track.uuid;

WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalogTypes AS text[]), CAST(@catalogUuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
)
SELECT
    song.*,
    artist.uuid AS artist_uuid,
    (
        SELECT COUNT(show_entity.id)::integer
        FROM setlist_songs_plays play
        JOIN setlist_shows setlist_show ON setlist_show.id = play.played_setlist_show_id
        LEFT JOIN shows show_entity
            ON show_entity.date = setlist_show.date
            AND show_entity.artist_id = song.artist_id
        WHERE play.played_setlist_song_id = song.id
    ) AS shows_played_at
FROM requested r
JOIN setlist_songs song ON r.catalog_type = 'song' AND song.uuid = r.catalog_uuid
JOIN artists artist ON artist.id = song.artist_id
JOIN features feature ON feature.artist_id = artist.id
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
ORDER BY venue.uuid;

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
ORDER BY year_entity.uuid;

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
    source_set.*,
    source.uuid AS source_uuid,
    artist.uuid AS artist_uuid
FROM source_sets source_set
JOIN candidate_source_ids candidate ON candidate.id = source_set.source_id
JOIN sources source ON source.id = source_set.source_id
JOIN artists artist ON artist.id = source.artist_id
ORDER BY source_set.source_id, source_set.index;
";
    }
}
