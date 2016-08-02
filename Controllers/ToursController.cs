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
            return await ApiRequest(artistIdOrSlug, (art) => {
                return _tourService.AllForArtistWithShowCount(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/venues/{idAndSlug}")]
        public async Task<IActionResult> years(string artistIdOrSlug, string idAndSlug)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, idAndSlug, (artist, id) => {
                return _tourService.ForIdWithShows(artist, id.Id.Value);
            });
        }
    }
}
