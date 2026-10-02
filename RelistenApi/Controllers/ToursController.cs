using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api")]
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

        [HttpGet("v2/artists/{artistIdOrSlug}/tours")]
        [ProducesResponseType(typeof(IEnumerable<TourWithShowCount>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Tours(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, art =>
            {
                return _tourService.AllForArtistWithShowCount(art);
            });
        }

        [HttpGet("v2/artists/{artistIdOrSlug}/tours/{idAndSlug}")]
        [ProducesResponseType(typeof(TourWithShows), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> ToursWithShows(string artistIdOrSlug, string idAndSlug)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, idAndSlug, (artist, id) =>
            {
                return _tourService.ForIdWithShows(artist, id.Id!.Value);
            });
        }

        [HttpGet("v3/artists/{artistIdOrSlug}/tours/{tourIdOrSlug}")]
        [ProducesResponseType(typeof(TourWithShows), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> ToursWithShowsV3(string artistIdOrSlug, string tourIdOrSlug)
        {
            var id = new Identifier(tourIdOrSlug);
            return await ApiRequest(artistIdOrSlug, async art =>
            {
                // Tour names commonly start with a year. Prefer that complete slug
                // before interpreting it as a legacy numeric-id-and-slug URL.
                if (id.Id.HasValue && id.Slug != null)
                {
                    var tour = await _tourService.ForIdWithShows(art, null, slug: tourIdOrSlug);
                    if (tour != null)
                    {
                        return tour;
                    }
                }

                var matchedTour = await _tourService.ForIdWithShows(art, id.Id, id.Guid, id.Slug);
                if (matchedTour == null && id.Id.HasValue && id.Slug == null)
                {
                    // Preserve bare numeric IDs, but allow numeric tour names such as "2012".
                    return await _tourService.ForIdWithShows(art, null, slug: tourIdOrSlug);
                }

                return matchedTour;
            });
        }
    }
}
