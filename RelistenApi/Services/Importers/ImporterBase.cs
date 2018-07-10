using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Dapper;
using Relisten.Vendor;
using Relisten.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Hangfire.Server;
using Hangfire.Console;
using System.Threading;

namespace Relisten.Import
{
    [Flags]
    public enum ImportableData
    {
        Nothing = 0,
        Eras = 1 << 0,
        Tours = 1 << 1,
        Venues = 1 << 2,
        SetlistShowsAndSongs = 1 << 3,
        SourceReviews = 1 << 4,
        SourceRatings = 1 << 5,
        Sources = 1 << 6
    }

    public class ImportStats
    {
        public static readonly ImportStats None = new ImportStats();

        public int Updated { get; set; } = 0;
        public int Created { get; set; } = 0;
        public int Removed { get; set; } = 0;

        public static ImportStats operator +(ImportStats c1, ImportStats c2)
        {
            return new ImportStats()
            {
                Updated = c1.Updated + c2.Updated,
                Removed = c1.Removed + c2.Removed,
                Created = c1.Created + c2.Created
            };
        }

        public override string ToString()
        {
            return $"Created: {Created}; Updated: {Updated}; Removed: {Removed}";
        }
    }

    public class ImporterService
    {
		IDictionary<string, ImporterBase> importers { get; set; }

        public ImporterService(
            ArchiveOrgImporter _archiveOrg,
            SetlistFmImporter _setlistFm,
			PhishinImporter _phishin,
			PhishNetImporter _phishnet,
			JerryGarciaComImporter _jerry,
			PanicStreamComImporter _panic,
			PhantasyTourImporter _phantasy
        )
        {
			var imps = new ImporterBase[] {
				_setlistFm,
				_archiveOrg,
				_panic,
				_jerry,
				_phishin,
				_phishnet,
				_phantasy
			};

			importers = new Dictionary<string, ImporterBase>();

			foreach (var i in imps)
			{
				importers[i.ImporterName] = i;
			}
        }

		public IEnumerable<ImporterBase> ImportersForArtist(Artist art)
		{
			return art.upstream_sources.Select(s => importers[s.upstream_source.name]);
		}

		public ImporterBase ImporterForUpstreamSource(UpstreamSource source)
		{
			return importers[source.name];
		}

		public async Task<ImportStats> Import(Artist artist, PerformContext ctx)
        {
			var stats = new ImportStats();

			var srcs = artist.upstream_sources.ToList();

			ctx?.WriteLine($"Found {srcs.Count} valid importers.");
			var prog = ctx?.WriteProgressBar();

			await srcs.AsyncForEachWithProgress(prog, async item =>
			{
				ctx?.WriteLine($"Importing with {item.upstream_source_id}, {item.upstream_identifier}");

				stats += await item.upstream_source.importer.ImportDataForArtist(artist, item, ctx);
			});

			return stats;
        }
    }

    public abstract class ImporterBase : IDisposable
    {
        protected DbService db { get; set; }
        protected HttpClient http { get; set; }

        public ImporterBase(DbService db)
        {
            this.db = db;
			http = new HttpClient();
	        
	        // iPhone on iOS 11.4
	        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 11_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/11.0 Mobile/15E148 Safari/604.1");
	        http.DefaultRequestHeaders.Add("Accept", "*/*");
        }

        public abstract ImportableData ImportableDataForArtist(Artist artist);
		public abstract Task<ImportStats> ImportDataForArtist(Artist artist, ArtistUpstreamSource src, PerformContext ctx);

		public abstract string ImporterName { get; }

        public void Dispose()
        {
            this.http.Dispose();
        }

		IDictionary<string, int> trackSlugCounts { get; set; } = new Dictionary<string, int>();

		/// <summary>
		/// Resets the slug counts. This needs to be called after each source is imported otherwise you'll get thing like you-enjoy-myself-624
		/// </summary>
		public void ResetTrackSlugCounts()
		{
			trackSlugCounts.Clear();
		}

        public string Slugify(string full)
        {
            var slug = Regex.Replace(full.ToLower().Normalize(), @"['.]", "");
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", " ");

            return Regex.Replace(slug, @"\s+", " ").
                Trim().
                Replace(" ", "-").
				Trim('-');
        }

		public string SlugifyTrack(string full)
		{
			var slug = Slugify(full);

            if(trackSlugCounts.ContainsKey(slug))
            {
				var count = 0;
				do
				{
					count = trackSlugCounts[slug];

					count++;
					trackSlugCounts[slug] = count;

					// keep incrementing until we find a slug that isn't taken
				} while(trackSlugCounts.ContainsKey(slug + $"-{count}"));

				slug += $"-{count}";
			}
            else
            {
                trackSlugCounts[slug] = 1;
            }

			return slug;
		}

        public async Task<ImportStats> RebuildYears(Artist artist)
        {
            await db.WithConnection(con => con.ExecuteAsync(@"
-- Drop years
DELETE FROM
	years
WHERE
	artist_id = @id
;

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
	
	(year, show_count, source_count, duration, avg_duration, avg_rating, artist_id, updated_at)
	
	SELECT
		to_char(s.date, 'YYYY') as year,
		COUNT(DISTINCT s.id) as show_count,
		COUNT(DISTINCT so.id) as source_count,
		SUM(so.duration) as duration,
		AVG(s.avg_duration) as avg_duration,
		AVG(s.avg_rating) as avg_rating,
		s.artist_id as artist_id,
		MAX(so.updated_at) as updated_at
	FROM
		shows s
		JOIN sources so ON so.show_id = s.id
	WHERE
		s.artist_id = @id
	GROUP BY
		s.artist_id, to_char(s.date, 'YYYY')
;

-- Associate shows with years
UPDATE
	shows s
SET
	year_id = y.id
FROM
	years y
WHERE
	y.year = to_char(s.date, 'YYYY')
    AND s.artist_id = @id
	AND y.artist_id = @id
;

REFRESH MATERIALIZED VIEW show_source_information;
REFRESH MATERIALIZED VIEW venue_show_counts;

            ", artist));
            return ImportStats.None;
        }
        public async Task<ImportStats> RebuildShows(Artist artist)
        {
			var sql = @"
-- Update durations
WITH durs AS (
	SELECT
		t.source_id,
		SUM(t.duration) as duration
	FROM
		source_tracks t
		JOIN sources s ON t.source_id = s.id
	WHERE
		s.artist_id = @id
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
;

-- Drop all the shows to rebuild them
DELETE
FROM
	shows	
WHERE
	artist_id = @id
;

UPDATE
	sources
SET
	show_id = NULL
WHERE
	artist_id = @id
;

-- Generate shows table without years or rating information
INSERT INTO
	shows
	
	(artist_id, date, display_date, updated_at, tour_id, era_id, venue_id, avg_duration)
	
	SELECT
		--array_agg(source.id) as srcs,
		--array_agg(setlist_show.id) as setls,
		--array_agg(source.display_date),
		--array_agg(setlist_show.date),
		source.artist_id,
		COALESCE(setlist_show.date, CASE
			WHEN MIN(source.display_date) LIKE '%X%' THEN to_date(MIN(source.display_date), 'YYYY')
			ELSE to_date(MIN(source.display_date), 'YYYY-MM-DD')
		END) as date,
		MIN(source.display_date) as display_date,
		MAX(source.updated_at) as updated_at,
		MAX(setlist_show.tour_id) as tour_id,
		MAX(setlist_show.era_id) as era_id,
		COALESCE(MAX(setlist_show.venue_id), MAX(source.venue_id)) as venue_id,
		MAX(source.duration) as avg_duration
	FROM
		sources source
		LEFT JOIN setlist_shows setlist_show ON to_char(setlist_show.date, 'YYYY-MM-DD') = source.display_date AND setlist_show.artist_id = @id
	WHERE
		(setlist_show.artist_id = @id OR setlist_show.artist_id IS NULL)
		AND source.artist_id = @id
		AND source.show_id IS NULL
	GROUP BY
		date, source.display_date, source.artist_id
	ORDER BY
		setlist_show.date
;

-- Associate sources with show
WITH show_assoc AS (
	SELECT
		src.id as source_id,
		sh.id as show_id
	FROM
		sources src
		JOIN shows sh ON src.display_date = sh.display_date AND sh.artist_id = @id
	WHERE
		src.artist_id = @id
)
UPDATE
	sources s
SET
	show_id = a.show_id
FROM
	show_assoc a
WHERE
	a.source_id = s.id
	AND s.artist_id = @id
;
			";

			if (artist.features.reviews_have_ratings)
			{
				sql += @"
	-- Update sources with calculated rating/review information
	WITH review_info AS (
	    SELECT
	        s.id as id,
	        COALESCE(AVG(rating), 0) as avg,
	        COUNT(r.rating) as num_reviews
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
		;

	-- Calculate weighted averages for sources once we have average data and update sources
	WITH review_info AS (
	    SELECT
	        s.id as id,
	        COALESCE(AVG(r.rating), 0) as avg,
	        COUNT(r.rating) as num_reviews
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
			SUM(s.num_reviews) as num_reviews,
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
	        (i_show.num_reviews * i_show.avg) + (i.num_reviews * i.avg) / (i_show.num_reviews + i.num_reviews + 1) as avg_rating_weighted
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
	    ;
				";
			}
			else {
				sql += @"
	-- used for things like Phish where ratings aren't attached to textual reviews
	-- Calculate weighted averages for sources once we have average data and update sources
	WITH show_info AS (
		SELECT
			s.show_id,
			SUM(s.num_ratings) as num_reviews,
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
	        (i_show.num_reviews * i_show.avg) + (s.num_ratings * s.avg_rating) / (i_show.num_reviews + s.num_ratings + 1) as avg_rating_weighted
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
	    ;
				";
			}

			sql += @"

-- Update shows with rating based on weighted averages
WITH rating_ranks AS (
	SELECT
		sh.id as show_id,
		s.avg_rating,
		s.updated_at as updated_at,
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
		AVG(avg_rating) as avg_rating,
		MAX(updated_at) as updated_at
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
	updated_at = r.updated_at
FROM max_rating r
WHERE
	r.show_id = s.id
	AND s.artist_id = @id
    ;

REFRESH MATERIALIZED VIEW show_source_information;
REFRESH MATERIALIZED VIEW venue_show_counts;
REFRESH MATERIALIZED VIEW source_review_counts;

            ";

            await db.WithConnection(con => con.ExecuteAsync(sql, artist));
            return ImportStats.None;
        }
    }
}
