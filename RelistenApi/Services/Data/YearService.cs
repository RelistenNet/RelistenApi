using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class YearService : RelistenDataServiceBase
    {
        ShowService _showService;

        public YearService(DbService db, ShowService showService) : base(db)
        {
            _showService = showService;
        }

        public async Task<IEnumerable<Year>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Year>(@"
                SELECT
                    *
                FROM
                    years
                WHERE
                    artist_id = @artistId
                ORDER BY
                    year ASC
            ", new { artistId = artist.id }));
        }

        public async Task<YearWithShows> ForIdentifierWithShows(Artist artist, Identifier id)
        {
            var year = await db.WithConnection(con => con.QuerySingleOrDefaultAsync<YearWithShows>(@"
                SELECT
                    *
                FROM
                    years y
                WHERE
                    y.artist_id = @artistId
                    AND " + (id.Id.HasValue ? "y.id = @year_id" : "y.year = @year") + @"
                ORDER BY
                    y.year ASC
            ", new { artistId = artist.id, year_id = id.Id, year = id.Slug }));

            if (year == null)
            {
                return null;
            }

            year.shows = new List<Show>();
            year.shows.AddRange(await _showService.ShowsForCriteria(artist,
                "s.artist_id = @artist_id AND s.year_id = @year_id",
                new { artist_id = artist.id, year_id = year.id }
            ));

            return year;
        }
    }
}