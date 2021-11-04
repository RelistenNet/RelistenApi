using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api/v2/artists")]
    [Produces("application/json")]
    public class VenuesController : RelistenBaseController
    {
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

        private VenueService _venueService { get; }
        private ShowService _showService { get; }

        [HttpGet("{artistIdOrSlug}/venues")]
        [ProducesResponseType(typeof(IEnumerable<VenueWithShowCount>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Venues(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, art =>
            {
                return _venueService.AllForArtist(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/venues/{idAndSlug}")]
        [ProducesResponseType(typeof(VenueWithShows), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Venues(string artistIdOrSlug, string idAndSlug)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, idAndSlug, (art, id) =>
            {
                return _venueService.ForIdWithShows(art, id.Id.Value);
            });
        }
    }
}
