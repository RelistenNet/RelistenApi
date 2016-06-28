using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;
using System.Linq.Expressions;

namespace Relisten.Controllers
{
    [Route("api/2/artists")]
    public class ToursController : RelistenBaseController
    {
        private TourService _tourService { get; set; }

        public ToursController(
            RedisService redis,
            DbService db,
            TourService tourService
        ) : base(redis, db)
        {
            _tourService = tourService;
        }

        [HttpGet("{artistIdOrSlug}/tours")]
        public async Task<IActionResult> tours(string artistIdOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                var tours = await db.WithConnection(con => con.QueryAsync<Tour>(@"
                    SELECT
                        t.*, COUNT(s.id) as shows_on_tour
                    FROM
                        tours t
                        LEFT JOIN setlist_shows s ON s.tour_id = t.id
                    WHERE
                        t.artist_id = @artistId
                    GROUP BY
                    	t.id
                    ORDER BY t.start_date
                ", new { artistId = art.id }));
                return JsonSuccess(tours);
            }

            return JsonNotFound();
        }
    }
}
