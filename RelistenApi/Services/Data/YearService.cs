using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class YearService : RelistenDataServiceBase
    {
        private readonly ShowService _showService;

        public YearService(DbService db, ShowService showService) : base(db)
        {
            _showService = showService;
        }

        public async Task<IEnumerable<Year>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Year>(@"
                SELECT
                    y.*
                    , a.uuid as artist_uuid
                FROM
                    years y
                    JOIN artists a ON a.id = y.artist_id
                WHERE
                    y.artist_id = @artistId
                ORDER BY
                    y.year ASC
            ", new {artistId = artist.id}));
        }

        public async Task<YearWithShows?> ForIdentifierWithShows(Artist artist, Identifier id)
        {
            var where = "";

            if (id.Id.HasValue)
            {
                where = "y.id = @year_id";
            } else if (id.Guid.HasValue)
            {
                where = "y.uuid = @year_guid";
            }
            else
            {
                where = "y.year = @year";
            }

            var year = await db.WithConnection(con => con.QuerySingleOrDefaultAsync<YearWithShows>(@$"
                SELECT
                    y.*
                    , a.uuid as artist_uuid
                FROM
                    years y
                    JOIN artists a ON a.id = y.artist_id
                WHERE
                    y.artist_id = @artistId
                    AND {where}
                ORDER BY
                    y.year ASC
            ", new {artistId = artist.id, year_id = id.Id, year = id.Slug, year_guid = id.Guid}));

            if (year == null)
            {
                return null;
            }

            year.shows = new List<Show>();
            year.shows.AddRange(await _showService.ShowsForCriteria(artist,
                "s.artist_id = @artist_id AND s.year_id = @year_id",
                new {artist_id = artist.id, year_id = year.id}
            ));

            return year;
        }
    }
}
