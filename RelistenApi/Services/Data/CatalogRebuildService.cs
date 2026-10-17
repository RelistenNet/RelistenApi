using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Controllers;

namespace Relisten.Data;

/// <summary>
/// Rebuilds derived show/year data and invalidates the artist cache. These SQL
/// operations own their transactions and must not run inside an item transaction.
/// </summary>
public sealed class CatalogRebuildService(DbService db, RedisService redisService)
{
    private const string IncrementalShowVenueRefreshSql =
        @"
-- Incremental refresh of show source info for this artist
WITH changed_show_source_information AS (
INSERT INTO
	show_source_information
	(show_id, max_updated_at, max_created_at, source_count, artist_id, max_avg_rating_weighted, has_soundboard_source, has_flac)
SELECT
	src.show_id,
	max(src.updated_at) AS max_updated_at,
	max(src.created_at) AS max_created_at,
	count(*) AS source_count,
	src.artist_id,
	max(src.avg_rating_weighted) AS max_avg_rating_weighted,
	bool_or(src.is_soundboard) AS has_soundboard_source,
	bool_or((src.flac_type = 1) OR (src.flac_type = 2)) AS has_flac
FROM sources src
WHERE src.artist_id = @id
GROUP BY src.show_id, src.artist_id
ON CONFLICT (show_id) DO UPDATE SET
	max_updated_at = EXCLUDED.max_updated_at,
	max_created_at = EXCLUDED.max_created_at,
	source_count = EXCLUDED.source_count,
	max_avg_rating_weighted = EXCLUDED.max_avg_rating_weighted,
	has_soundboard_source = EXCLUDED.has_soundboard_source,
	has_flac = EXCLUDED.has_flac
WHERE
	(
		show_source_information.max_updated_at,
		show_source_information.max_created_at,
		show_source_information.source_count,
		show_source_information.max_avg_rating_weighted,
		show_source_information.has_soundboard_source,
		show_source_information.has_flac
	) IS DISTINCT FROM (
		EXCLUDED.max_updated_at,
		EXCLUDED.max_created_at,
		EXCLUDED.source_count,
		EXCLUDED.max_avg_rating_weighted,
		EXCLUDED.has_soundboard_source,
		EXCLUDED.has_flac
	)
RETURNING show_id
)
UPDATE shows sh
SET updated_at = GREATEST(sh.updated_at, statement_timestamp())
FROM changed_show_source_information changed
WHERE sh.id = changed.show_id
;

-- Incremental refresh of venue show counts for this artist
WITH source_shows AS (
	SELECT
		venue_id,
		count(DISTINCT show_id) AS source_show_count
	FROM sources
	WHERE artist_id = @id
	GROUP BY venue_id
), show_counts AS (
	SELECT
		venue_id,
		count(*) AS show_count
	FROM shows sh
	WHERE artist_id = @id AND EXISTS(SELECT 1 FROM sources src WHERE src.show_id=sh.id)
	GROUP BY venue_id
)
INSERT INTO venue_show_counts
	(id, shows_at_venue)
SELECT
	v.id,
	CASE
		WHEN coalesce(ss.source_show_count, 0) = 0 THEN coalesce(sc.show_count, 0)
		ELSE ss.source_show_count
	END AS shows_at_venue
FROM venues v
LEFT JOIN source_shows ss ON ss.venue_id = v.id
LEFT JOIN show_counts sc ON sc.venue_id = v.id
WHERE v.artist_id = @id OR v.artist_id IS NULL
ON CONFLICT (id) DO UPDATE SET
	shows_at_venue = EXCLUDED.shows_at_venue
WHERE
	venue_show_counts.shows_at_venue IS DISTINCT FROM EXCLUDED.shows_at_venue
;
";

    private const string IncrementalSourceReviewRefreshSql =
        @"
-- Incremental refresh of source review counts for this artist
INSERT INTO
	source_review_counts
	(source_id, source_review_max_updated_at, source_review_count)
SELECT
	r.source_id,
	max(r.updated_at) AS source_review_max_updated_at,
	count(r.id) AS source_review_count
FROM source_reviews r
JOIN sources s ON s.id = r.source_id
WHERE s.artist_id = @id
GROUP BY r.source_id
ON CONFLICT (source_id) DO UPDATE SET
	source_review_max_updated_at = EXCLUDED.source_review_max_updated_at,
	source_review_count = EXCLUDED.source_review_count
WHERE
	(
		source_review_counts.source_review_max_updated_at,
		source_review_counts.source_review_count
	) IS DISTINCT FROM (
		EXCLUDED.source_review_max_updated_at,
		EXCLUDED.source_review_count
	)
;
";

    /// <summary>Refreshes year aggregates after catalog writes.</summary>
    public async Task RebuildYears(Artist artist)
    {
        await db.WithWriteConnection(con =>
            con.ExecuteAsync(
                @"
BEGIN TRANSACTION;

-- Build Years
WITH year_sources AS (
	SELECT
		to_char(sh.date, 'YYYY') as year,
		COUNT(*) as source_count
	FROM
		shows sh
		JOIN sources s ON sh.id = s.show_id
	WHERE
		sh.artist_id = @id
	GROUP BY
		to_char(sh.date, 'YYYY')
)

INSERT INTO
	years

	(year, show_count, source_count, duration, avg_duration, avg_rating, artist_id, updated_at, uuid)

	SELECT
		to_char(s.date, 'YYYY') as year,
		COUNT(DISTINCT s.id) as show_count,
		COUNT(DISTINCT so.id) as source_count,
		SUM(so.duration) as duration,
		AVG(s.avg_duration) as avg_duration,
		AVG(s.avg_rating) as avg_rating,
		s.artist_id as artist_id,
		MAX(so.updated_at) as updated_at,
		md5(s.artist_id || '::year::' || to_char(s.date, 'YYYY'))::uuid as uuid
	FROM
		shows s
		JOIN sources so ON so.show_id = s.id
	WHERE
		s.artist_id = @id
	GROUP BY
		s.artist_id, to_char(s.date, 'YYYY')
ON CONFLICT ON CONSTRAINT years_uuid_key
DO
	UPDATE SET -- don't update artist_id or year since uuid is based on that
		show_count = EXCLUDED.show_count,
		source_count = EXCLUDED.source_count,
		duration = EXCLUDED.duration,
		avg_duration = EXCLUDED.avg_duration,
		avg_rating = EXCLUDED.avg_rating,
		updated_at = EXCLUDED.updated_at
WHERE
	(
		years.show_count,
		years.source_count,
		years.duration,
		years.avg_duration,
		years.avg_rating,
		years.updated_at
	) IS DISTINCT FROM (
		EXCLUDED.show_count,
		EXCLUDED.source_count,
		EXCLUDED.duration,
		EXCLUDED.avg_duration,
		EXCLUDED.avg_rating,
		EXCLUDED.updated_at
	)
;

-- Associate shows with years (must happen before the DELETE below)
UPDATE
	shows s
SET
	year_id = y.id,
	updated_at = GREATEST(s.updated_at, statement_timestamp())
FROM
	years y
WHERE
	y.year = to_char(s.date, 'YYYY')
    AND s.artist_id = @id
	AND y.artist_id = @id
	AND s.year_id IS DISTINCT FROM y.id
;

-- Retained identities without recordings are not visible years or counted performances.
UPDATE shows sh SET year_id=NULL WHERE sh.artist_id=@id
    AND NOT EXISTS(SELECT 1 FROM sources src WHERE src.show_id=sh.id);

-- remove years that no longer have recorded shows
DELETE FROM years y
WHERE
	y.artist_id = @id
	AND NOT EXISTS (
		SELECT 1
		FROM shows s
		WHERE s.artist_id = @id
			AND s.year_id = y.id
            AND EXISTS(SELECT 1 FROM sources src WHERE src.show_id=s.id)
	);
COMMIT;

" + IncrementalShowVenueRefreshSql,
                new { artist.id }
            )
        );
    }

    /// <summary>Removes the cached artist response after show and year rebuilds succeed.</summary>
    public Task InvalidateArtistCache(Artist artist) =>
        redisService.db.KeyDeleteAsync(ArtistsController.FullArtistCacheKey(artist));

    /// <summary>Refreshes shows, recording aggregates, and derived counts for one artist.</summary>
    public async Task RebuildShows(Artist artist)
    {
        var sql =
            @"
BEGIN;
SELECT pg_advisory_xact_lock(1381191750,@id);

-- Update durations
WITH durs AS (
	SELECT
		t.source_id,
		SUM(t.duration) as duration
	FROM
		source_tracks t
	WHERE
		t.artist_id = @id
        AND t.is_orphaned != true
	GROUP BY
		t.source_id
)
UPDATE
	sources s
SET
	duration = d.duration
FROM
	durs d
WHERE
	s.id = d.source_id
	AND s.artist_id =  @id
	AND s.duration IS DISTINCT FROM d.duration
;

-- Resolve durable assignments before deriving aggregates. Assignment SQL shares the
-- artist mutation lock with reviewed/provider writes and never regenerates accepted IDs.

			";

        sql +=
            ShowPerformanceService.SynchronizeSql
            + """
                UPDATE shows sh SET
                    venue_id=aggregate.venue_id,tour_id=aggregate.tour_id,era_id=aggregate.era_id,
                    avg_duration=aggregate.duration,updated_at=statement_timestamp()
                FROM (
                    SELECT src.show_id,
                        COALESCE(max(sl.venue_id),max(src.venue_id) FILTER(
                            WHERE a.assignment_kind='accepted' OR (a.assignment_kind='legacy' AND
                                (SELECT count(*) FROM shows other WHERE other.artist_id=@id AND other.display_date=src.display_date)=1))) AS venue_id,
                        max(sl.tour_id) AS tour_id,max(sl.era_id) AS era_id,max(src.duration) AS duration
                    FROM sources src JOIN source_show_assignments a ON a.source_id=src.id
                    LEFT JOIN setlist_show_assignments sa ON sa.show_id=src.show_id
                    LEFT JOIN setlist_shows sl ON sl.id=sa.setlist_show_id
                    WHERE src.artist_id=@id GROUP BY src.show_id
                ) aggregate
                WHERE sh.id=aggregate.show_id AND
                    (sh.venue_id,sh.tour_id,sh.era_id,sh.avg_duration) IS DISTINCT FROM
                    (aggregate.venue_id,aggregate.tour_id,aggregate.era_id,aggregate.duration);
                """;

        if (artist.features.reviews_have_ratings)
        {
            sql +=
                @"
	-- Update sources with calculated rating/review information
		WITH review_info AS (
		    SELECT
		        s.id as id,
		        COALESCE(AVG(NULLIF(r.rating, 0)), 0) as avg,
		        COUNT(r.id) as num_reviews
		    FROM
		        sources s
		        LEFT JOIN source_reviews r ON r.source_id = s.id
		WHERE
			s.artist_id = @id
	    GROUP BY
	        s.id
	    ORDER BY
	    	s.id
	)
	UPDATE
		sources s
	SET
		avg_rating = i.avg,
		num_reviews = i.num_reviews
	FROM
		review_info i
	WHERE
		s.id = i.id
		AND s.artist_id = @id
		AND (s.avg_rating, s.num_reviews) IS DISTINCT FROM (i.avg, i.num_reviews)
		;

	-- Calculate weighted averages for sources once we have average data and update sources
		WITH review_info AS (
		    SELECT
		        s.id as id,
		        COALESCE(AVG(NULLIF(r.rating, 0)), 0) as avg,
		        COUNT(r.id) as num_reviews
		    FROM
		        sources s
		        LEFT JOIN source_reviews r ON r.source_id = s.id
		WHERE
			s.artist_id = @id
	    GROUP BY
	        s.id
	    ORDER BY
	    	s.id
	), show_info AS (
		SELECT
			s.show_id,
			SUM(LN(s.num_reviews + 1)) as num_reviews,
			AVG(s.avg_rating) as avg
		FROM
			sources s
		WHERE
			s.artist_id = @id
		GROUP BY
			s.show_id
	), weighted_info AS (
	    SELECT
	        s.id as id,
	        i_show.show_id,
	        i.num_reviews,
	        i.avg,
	        i_show.num_reviews,
	        i_show.avg,
	        (i_show.num_reviews * i_show.avg + LN(i.num_reviews + 1) * i.avg) / (i_show.num_reviews + LN(i.num_reviews + 1) + 1) as avg_rating_weighted
	    FROM
	        sources s
			LEFT JOIN review_info i ON i.id = s.id
			LEFT JOIN show_info i_show ON i_show.show_id = s.show_id
		WHERE
			s.artist_id = @id
	    GROUP BY
	        s.id, i_show.show_id, i.num_reviews, i.avg, i_show.num_reviews, i_show.avg
	    ORDER BY
	    	s.id
	)

	UPDATE
		sources s
	SET
		avg_rating_weighted = i.avg_rating_weighted
	FROM
		weighted_info i
	WHERE
		i.id = s.id
		AND s.artist_id = @id
		AND s.avg_rating_weighted IS DISTINCT FROM i.avg_rating_weighted
	    ;
				";
        }
        else
        {
            sql +=
                @"
	-- used for things like Phish where ratings aren't attached to textual reviews
	-- Calculate weighted averages for sources once we have average data and update sources
	WITH show_info AS (
		SELECT
			s.show_id,
			SUM(LEAST(s.num_ratings, 500)) as num_reviews,
			AVG(s.avg_rating) as avg
		FROM
			sources s
		WHERE
			s.artist_id = @id
		GROUP BY
			s.show_id
	), weighted_info AS (
	    SELECT
	        s.id as id,
	        i_show.show_id,
	        s.num_ratings,
	        s.avg_rating,
	        i_show.num_reviews,
	        i_show.avg,
	        (i_show.num_reviews * i_show.avg + LEAST(s.num_ratings, 500) * s.avg_rating) / (i_show.num_reviews + LEAST(s.num_ratings, 500) + 1) as avg_rating_weighted
	    FROM
	        sources s
			LEFT JOIN show_info i_show ON i_show.show_id = s.show_id
		WHERE
			s.artist_id = @id
	    GROUP BY
	        s.id, i_show.show_id, s.num_ratings, s.avg_rating, i_show.num_reviews, i_show.avg
	    ORDER BY
	    	s.id
	)

	UPDATE
		sources s
	SET
		avg_rating_weighted = i.avg_rating_weighted
	FROM
		weighted_info i
	WHERE
		i.id = s.id
		AND s.artist_id = @id
		AND s.avg_rating_weighted IS DISTINCT FROM i.avg_rating_weighted
	    ;
				";
        }

        sql +=
            @"

-- Update shows with rating based on weighted averages
WITH rating_ranks AS (
	SELECT
		sh.id as show_id,
		s.avg_rating,
		s.num_reviews,
		s.avg_rating_weighted,
		RANK() OVER (PARTITION BY sh.id ORDER BY s.avg_rating_weighted DESC) as rank
	FROM
		sources s
		JOIN shows sh ON s.show_id = sh.id
	WHERE
		s.artist_id = @id
), max_rating AS (
	SELECT
		show_id,
		AVG(avg_rating)::real as avg_rating
	FROM
		rating_ranks
	WHERE
		rank = 1
	GROUP BY
		show_id
)

UPDATE
	shows s
SET
	avg_rating = r.avg_rating,
	-- A changed aggregate is a catalog change even when no upstream source timestamp moved.
	updated_at = GREATEST(s.updated_at, statement_timestamp())
FROM max_rating r
WHERE
	r.show_id = s.id
	AND s.artist_id = @id
	AND s.avg_rating IS DISTINCT FROM r.avg_rating
    ;

-- Keep accepted show rows even when an upstream deletion temporarily removes every
-- recording. Their UUIDs and provider keys must remain usable if that recording returns.
DELETE FROM show_source_information info WHERE info.artist_id=@id
    AND NOT EXISTS(SELECT 1 FROM sources src WHERE src.show_id=info.show_id);

COMMIT;
";

        sql += IncrementalShowVenueRefreshSql + IncrementalSourceReviewRefreshSql;

        await db.WithWriteConnection(con => con.ExecuteAsync(sql, new { artist.id }));
    }
}
