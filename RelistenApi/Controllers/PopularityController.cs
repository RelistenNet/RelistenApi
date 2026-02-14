using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Services.Popularity;

namespace Relisten.Controllers
{
    [Route("api")]
    [Produces("application/json")]
    public class PopularityController : RelistenBaseController
    {
        private const int MaxShowLimit = 25;
        private readonly PopularityService popularityService;

        public PopularityController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            PopularityService popularityService
        ) : base(redis, db, artistService)
        {
            this.popularityService = popularityService;
        }

        [HttpGet("v3/popular/artists")]
        [ProducesResponseType(typeof(PopularArtistListItem[]), 200)]
        public async Task<IActionResult> PopularArtists([FromQuery] int limit = 50)
        {
            return JsonSuccess(await popularityService.GetPopularArtists(limit));
        }

        [HttpGet("v3/trending/artists")]
        [ProducesResponseType(typeof(PopularArtistListItem[]), 200)]
        public async Task<IActionResult> TrendingArtists([FromQuery] int limit = 50)
        {
            return JsonSuccess(await popularityService.GetTrendingArtists(limit));
        }

        [HttpGet("v3/popular/shows")]
        [ProducesResponseType(typeof(Show[]), 200)]
        public async Task<IActionResult> PopularShows([FromQuery] int limit = MaxShowLimit)
        {
            return JsonSuccess(await popularityService.GetPopularShows(NormalizeShowLimit(limit)));
        }

        [HttpGet("v3/trending/shows")]
        [ProducesResponseType(typeof(Show[]), 200)]
        public async Task<IActionResult> TrendingShows([FromQuery] int limit = MaxShowLimit)
        {
            return JsonSuccess(await popularityService.GetTrendingShows(NormalizeShowLimit(limit)));
        }

        [HttpGet("v3/artists/{artistIdOrSlug}/shows/popular-trending")]
        [ProducesResponseType(typeof(ArtistPopularTrendingShowsResponse), 200)]
        public async Task<IActionResult> ArtistPopularTrendingShows([FromRoute] string artistIdOrSlug,
            [FromQuery] int limit = MaxShowLimit)
        {
            return await ApiRequest(artistIdOrSlug,
                art => popularityService.GetArtistPopularTrendingShows(art, NormalizeShowLimit(limit)));
        }

        [HttpGet("v3/artists/shows/popular-trending")]
        [ProducesResponseType(typeof(MultiArtistPopularTrendingShowsResponse), 200)]
        public async Task<IActionResult> ArtistsPopularTrendingShows([FromQuery] string[]? artistIds = null,
            [FromQuery] int limit = MaxShowLimit)
        {
            return await ApiRequest(artistIds ?? [],
                arts => popularityService.GetArtistsPopularTrendingShows(arts, NormalizeShowLimit(limit)),
                queryAllWhenEmpty: false);
        }

        private static int NormalizeShowLimit(int limit)
        {
            if (limit <= 0)
            {
                return MaxShowLimit;
            }

            return limit > MaxShowLimit ? MaxShowLimit : limit;
        }

        [HttpGet("v3/popular/years")]
        [ProducesResponseType(typeof(PopularYearListItem[]), 200)]
        public async Task<IActionResult> PopularYears([FromQuery] int limit = 50)
        {
            return JsonSuccess(await popularityService.GetPopularYears(limit));
        }

        [HttpGet("v3/trending/years")]
        [ProducesResponseType(typeof(PopularYearListItem[]), 200)]
        public async Task<IActionResult> TrendingYears([FromQuery] int limit = 50)
        {
            return JsonSuccess(await popularityService.GetTrendingYears(limit));
        }
    }
}
