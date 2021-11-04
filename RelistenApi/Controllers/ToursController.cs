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
    public class ToursController : RelistenBaseController
    {
        public ToursController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            TourService tourService
        ) : base(redis, db, artistService)
        {
            _tourService = tourService;
        }

        private TourService _tourService { get; }

        [HttpGet("{artistIdOrSlug}/tours")]
        [ProducesResponseType(typeof(IEnumerable<TourWithShowCount>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> tours(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, art =>
            {
                return _tourService.AllForArtistWithShowCount(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/tours/{idAndSlug}")]
        [ProducesResponseType(typeof(TourWithShows), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> tours(string artistIdOrSlug, string idAndSlug)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, idAndSlug, (artist, id) =>
            {
                return _tourService.ForIdWithShows(artist, id.Id.Value);
            });
        }
    }
}
