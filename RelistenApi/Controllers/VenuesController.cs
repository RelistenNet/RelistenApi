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
    public class VenuesController : RelistenBaseController
    {
        private VenueService _venueService { get; set; }
        private ShowService _showService { get; set; }

        public VenuesController(
            RedisService redis,
            DbService db,
			ArtistService artistService,
            VenueService venueService,
            ShowService showService
		) : base(redis, db, artistService)
        {
            _venueService = venueService;
            _showService = showService;
        }

        [HttpGet("{artistIdOrSlug}/venues")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<VenueWithShowCount>>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> tours(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, (art) => {
                return _venueService.AllForArtist(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/venues/{idAndSlug}")]
        [ProducesResponseType(typeof(ResponseEnvelope<VenueWithShows>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> years(string artistIdOrSlug, string idAndSlug)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, idAndSlug, (art, id) => {
                return _venueService.ForIdWithShows(art, id.Id.Value);
            });
        }
    }
}
