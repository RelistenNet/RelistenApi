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

        public async Task<IEnumerable<T>> ShowsForCriteriaGeneric<T>(Artist artist, string where, object parms) where T : Show
        {
            return await db.WithConnection(con => con.QueryAsync<T, Venue, Tour, Era, T>(@"
                    SELECT
                        s.*, cnt.sources_count, v.*, t.*, e.*
                    FROM
                        shows s
                        LEFT JOIN venues v ON s.venue_id = v.id
                        LEFT JOIN tours t ON s.tour_id = t.id
                        LEFT JOIN eras e ON s.era_id = e.id
                        INNER JOIN (
                        	SELECT
                        		src.show_id, COUNT(*) as sources_count
                        	FROM
                        		sources src
                        	GROUP BY
                        		src.show_id
                        ) cnt ON cnt.show_id = s.id
                    WHERE
                        " + where + @"
                    ORDER BY
                        display_date ASC
                ", (Show, venue, tour, era) =>
            {
                Show.venue = venue;

                if (artist.features.tours)
                {
                    Show.tour = tour;
                }

                if (artist.features.eras)
                {
                    Show.era = era;
                }

                return Show;
            }, parms));
        }

        public async Task<IEnumerable<Show>> ShowsForCriteria(Artist artist, string where, object parms)
        {
            return await ShowsForCriteriaGeneric<Show>(artist, where, parms);
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

        public async Task<IEnumerable<Show>> AllForArtist(Artist artist, bool withVenuesToursAndEras = false)
        {
            if (withVenuesToursAndEras)
            {
                return await db.WithConnection(con => con.QueryAsync<Show, Tour, Venue, Era, Show>(@"
                    SELECT
                        s.*, t.*, v.*, e.*
                    FROM
                        setlist_shows s
                        LEFT JOIN tours t ON s.tour_id = t.id
                        LEFT JOIN venues v ON s.venue_id = v.id
                        LEFT JOIN eras e ON s.era_id = e.id
                    WHERE
                        s.artist_id = @id
                    ", (Show, tour, venue, era) =>
                {
                    Show.venue = venue;

                    if (artist.features.tours)
                    {
                        Show.tour = tour;
                    }

                    if (artist.features.eras)
                    {
                        Show.era = era;
                    }

                    return Show;
                }, artist));
            }

            return await db.WithConnection(con => con.QueryAsync<Show>(@"
                SELECT
                    *
                FROM
                    setlist_shows
                WHERE
                    artist_id = @id
            ", artist));
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