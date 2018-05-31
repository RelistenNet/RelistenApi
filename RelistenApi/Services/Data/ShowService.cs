using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class ShowService : RelistenDataServiceBase
    {
        private SourceService _sourceService { get; set; }

        public ShowService(
            DbService db,
            SourceService sourceService
        ) : base(db)
        {
            _sourceService = sourceService;
        }

        public async Task<IEnumerable<T>> ShowsForCriteriaGeneric<T>(
            Artist artist,
            string where,
            object parms,
			int? limit = null,
			string orderBy = null
        ) where T : Show
        {
			orderBy = orderBy == null ? "display_date ASC" : orderBy;
			var limitClause = limit == null ? "" : "LIMIT " + limit;

			return await db.WithConnection(con => con.QueryAsync<T, VenueWithShowCount, Tour, Era, Year, T>(@"
                SELECT
                    s.*,
                    cnt.max_updated_at as most_recent_source_updated_at,
					cnt.source_count,
                    cnt.has_soundboard_source,
                    cnt.has_flac as has_streamable_flac_source,
					v.*,
                    venue_counts.shows_at_venue,
					t.*,
					e.*,
                    y.*
                FROM
                    shows s
                    LEFT JOIN venues v ON s.venue_id = v.id
                    LEFT JOIN tours t ON s.tour_id = t.id
                    LEFT JOIN eras e ON s.era_id = e.id
                    LEFT JOIN years y ON s.year_id = y.id
                    INNER JOIN (
                    	SELECT
                    		src.show_id,
							MAX(src.updated_at) as max_updated_at,
							COUNT(*) as source_count,
							MAX(src.avg_rating_weighted) as max_avg_rating_weighted,
							BOOL_OR(src.is_soundboard) as has_soundboard_source,
							BOOL_OR(CASE WHEN src.flac_type = 1 OR src.flac_type = 2 THEN true ELSE false END) as has_flac
                    	FROM
                    		sources src
                    	GROUP BY
                    		src.show_id
                    ) cnt ON cnt.show_id = s.id

                    INNER JOIN (
                        SELECT
                            v.id
        
                            , CASE
                                WHEN COUNT(DISTINCT src.show_id) = 0 THEN
                                    COUNT(s.id)
                                ELSE
                                    COUNT(DISTINCT src.show_id)
                            END as shows_at_venue
                        FROM
                            shows s
                            JOIN venues v ON v.id = s.venue_id
                            LEFT JOIN sources src ON src.venue_id = v.id
                        GROUP BY
                            v.id
                    ) venue_counts ON venue_counts.id = s.venue_id
                WHERE
                    " + where + @"
                ORDER BY
                    " + orderBy + @"
				" + limitClause + @"
            ", (Show, venue, tour, era, year) =>
            {
                Show.venue = venue;

                if (artist == null || artist.features.tours)
                {
                    Show.tour = tour;
                }

                if (artist == null || artist.features.eras)
                {
                    Show.era = era;
                }

                if (artist == null || artist.features.years)
                {
                    Show.year = year;
                }

                return Show;
            }, parms));
        }

        public async Task<IEnumerable<Show>> ShowsForCriteria(
            Artist artist,
            string where,
            object parms,
			int? limit = null,
			string orderBy = null)
        {
			return await ShowsForCriteriaGeneric<Show>(artist, where, parms, limit, orderBy);
        }

        public async Task<IEnumerable<ShowWithArtist>> ShowsForCriteriaWithArtists(string where, object parms, int? limit = null, string orderBy = null)
        {
			orderBy = orderBy == null ? "display_date ASC" : orderBy;
			var limitClause = limit == null ? "" : "LIMIT " + limit;

            return await db.WithConnection(con => con.QueryAsync<ShowWithArtist, VenueWithShowCount, Tour, Era, Artist, Features, ShowWithArtist>(@"
                    SELECT
                        s.*,
                        cnt.max_updated_at as most_recent_source_updated_at,
						cnt.source_count,
						cnt.has_soundboard_source,
						v.*,
                        venue_counts.shows_at_venue,
						t.*,
						e.*,
						a.*
                    FROM
                        shows s
                        LEFT JOIN venues v ON s.venue_id = v.id
                        LEFT JOIN tours t ON s.tour_id = t.id
                        LEFT JOIN eras e ON s.era_id = e.id
                        LEFT JOIN (
                        	SELECT
                        		arts.id as aid, arts.*, f.*
                        	FROM
                        		artists arts
                        		INNER JOIN features f ON f.artist_id = arts.id
                        ) a ON s.artist_id = a.aid
                        INNER JOIN (
                        	SELECT
                        		src.show_id,
                                MAX(src.updated_at) as max_updated_at,
								COUNT(*) as source_count,
								BOOL_OR(src.is_soundboard) as has_soundboard_source
                        	FROM
                        		sources src
                        	GROUP BY
                        		src.show_id
                        ) cnt ON cnt.show_id = s.id

                        INNER JOIN (
                            SELECT
                                v.id
            
                                , CASE
                                    WHEN COUNT(DISTINCT src.show_id) = 0 THEN
                                        COUNT(s.id)
                                    ELSE
                                        COUNT(DISTINCT src.show_id)
                                END as shows_at_venue
                            FROM
                                shows s
                                JOIN venues v ON v.id = s.venue_id
                                LEFT JOIN sources src ON src.venue_id = v.id
                            GROUP BY
                                v.id
                        ) venue_counts ON venue_counts.id = s.venue_id
                    WHERE
                        " + where + @"
                    ORDER BY
                        " + orderBy + @"
                    " + limitClause + @"
                ", (Show, venue, tour, era, art, features) =>
            {
                art.features = features;
                Show.artist = art;
                Show.venue = venue;

                if (art.features.tours)
                {
                    Show.tour = tour;
                }

                if (art.features.eras)
                {
                    Show.era = era;
                }

                return Show;
            }, parms));
        }

        public async Task<ShowWithSources> ShowWithSourcesForArtistOnDate(Artist artist, string displayDate)
        {
            var shows = await ShowsForCriteriaGeneric<ShowWithSources>(artist,
                "s.artist_id = @artistId AND s.display_date = @showDate",
                new { artistId = artist.id, showDate = displayDate }
            );
            var show = shows.FirstOrDefault();

            if (show == null)
            {
                return null;
            }

            show.sources = await _sourceService.FullSourcesForShow(artist, show);

            return show;
        }

        public async Task<IEnumerable<Show>> AllSimpleForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Show>(@"
                SELECT
                    id, created_at, updated_at, date
                FROM
                    setlist_shows
                WHERE
                    artist_id = @id
            ", artist));
        }
    }
}