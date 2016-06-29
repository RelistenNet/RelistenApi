UPDATE
	sources
SET
	show_id = NULL
	;

-- Drop all the shows to rebuild them
DELETE
FROM
	shows	
WHERE
	artist_id =2
;

-- Generate shows table without years or rating information
INSERT INTO
	shows
	
	(artist_id, date, display_date, updated_at, tour_id, era_id, venue_id)
	
	SELECT
		setlist_show.artist_id,
		setlist_show.date,
		MIN(source.display_date) as display_date,
		MAX(source.updated_at) as updated_at,
		setlist_show.tour_id,
		setlist_show.era_id,
		setlist_show.venue_id
	FROM
		sources source
		JOIN setlist_shows setlist_show ON to_char(setlist_show.date, 'YYYY-MM-DD') = source.display_date
	WHERE
		setlist_show.artist_id = 2
		AND source.show_id IS NULL
	GROUP BY
		setlist_show.artist_id, setlist_show.date, setlist_show.tour_id, setlist_show.era_id, setlist_show.venue_id
	ORDER BY
		setlist_show.date
;

-- Update sources with calculated rating/review information
WITH review_info AS (
    SELECT
        s.id as id,
        COALESCE(AVG(rating), 0) as avg,
        COUNT(r.rating) as num_reviews
    FROM
        sources s
        LEFT JOIN source_reviews r ON r.source_id = s.id
    GROUP BY
        s.id
    ORDER BY
    	s.id 
)
UPDATE
	sources
SET
	avg_rating = (SELECT avg FROM review_info i where i.id = sources.id),
	num_reviews = (SELECT num_reviews FROM review_info i where i.id = sources.id)
WHERE
	artist_id = 2
;

-- Calculate weighted averages for sources once we have average data
WITH review_info AS (
    SELECT
        s.id as id,
        COALESCE(AVG(r.rating), 0) as avg,
        COUNT(r.rating) as num_reviews
    FROM
        sources s
        LEFT JOIN source_reviews r ON r.source_id = s.id
    GROUP BY
        s.id
    ORDER BY
    	s.id 
), weighted_info AS (
    SELECT
        s.id as id,
        (AVG(i_show.num_reviews) * AVG(i_show.avg)) + (i.num_reviews * i.avg) / (AVG(i_show.num_reviews) + i.num_reviews + 1) as avg_rating_weighted
    FROM
        sources s
		JOIN review_info i ON i.id = s.id
		JOIN review_info i_show ON i_show.id IN (SELECT id FROM sources WHERE show_id = s.show_id)
    GROUP BY
        s.id, i.num_reviews, i.avg
    ORDER BY
    	s.id 
)

UPDATE
	sources
SET
	avg_rating_weighted = (SELECT avg_rating_weighted FROM weighted_info i where i.id = sources.id)
WHERE
	artist_id = 2
    ;

-- Associate sources with show
WITH show_assoc AS (
	SELECT
		src.id as source_id,
		sh.id as show_id
	FROM
		sources src
		JOIN shows sh ON src.display_date = sh.display_date
)
UPDATE
	sources
SET
	show_id = (SELECT show_id FROM show_assoc a WHERE a.source_id = sources.id)
;

SELECT
	setlist_show.artist_id, setlist_show.date, AVG(source.rating) as avg_rating
FROM
	sources source
	LEFT JOIN setlist_shows setlist_show ON to_char(setlist_show.date, 'YYYY-MM-DD') = source.display_date WHERE setlist_show.artist_id = 2
GROUP BY
	setlist_show.artist_id, setlist_show.date
ORDER BY
	setlist_show.date

