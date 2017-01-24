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
using Relisten.Api.Models.Api;

namespace Relisten.Controllers
{
    [Route("api/v2/artists")]
    [Produces("application/json")]
    public class ToursController : RelistenBaseController
    {
        private TourService _tourService { get; set; }

        public ToursController(
            RedisService redis,
            DbService db,
			ArtistService artistService,
            TourService tourService
		) : base(redis, db, artistService)
        {
            _tourService = tourService;
        }

        [HttpGet("{artistIdOrSlug}/tours")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<TourWithShowCount>>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> tours(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, (art) => {
                return _tourService.AllForArtistWithShowCount(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/tours/{idAndSlug}")]
        [ProducesResponseType(typeof(ResponseEnvelope<TourWithShows>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> tours(string artistIdOrSlug, string idAndSlug)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, idAndSlug, (artist, id) => {
                return _tourService.ForIdWithShows(artist, id.Id.Value);
            });
        }
    }
}
