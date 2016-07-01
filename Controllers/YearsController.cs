using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api/2/artists")]
    public class YearsController : RelistenBaseController
    {
        protected ShowService _showService;

        public YearsController(
            RedisService redis,
            DbService db,
            ShowService showService
        ) : base(redis, db) {
            _showService = showService;
        }

        [HttpGet("{artistIdOrSlug}/years")]
        public async Task<IActionResult> years(string artistIdOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                var tours = await db.WithConnection(con => con.QueryAsync<Year>(@"
                    SELECT
                        *
                    FROM
                        years
                    WHERE
                        artist_id = @artistId
                    ORDER BY
                        year ASC
                ", new { artistId = art.id }));
                return JsonSuccess(tours);
            }

            return JsonNotFound();
        }

        [HttpGet("{artistIdOrSlug}/years/{idAndOrYear}")]
        public async Task<IActionResult> years(string artistIdOrSlug, string idAndOrYear)
        {
            Artist art = await FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                var id = new Identifier(idAndOrYear);
                var year = await db.WithConnection(con => con.QuerySingleOrDefaultAsync<Year>(@"
                    SELECT
                        *
                    FROM
                        years y
                    WHERE
                        y.artist_id = @artistId
                        AND " + (id.Id.HasValue ? "y.id = @year_id" : "y.year = @year") + @"
                    ORDER BY
                        y.year ASC
                ", new { artistId = art.id, year_id = id.Id, year = id.Slug }));

                if(year == null)
                {
                    return JsonNotFound();
                }

                year.shows = new List<Show>();
                year.shows.AddRange(await _showService.ShowsForCriteria(
                    "s.year_id = @year_id",
                    new { year_id = year.id }
                ));

                return JsonSuccess(year);
            }

            return JsonNotFound();
        }
    }
}
