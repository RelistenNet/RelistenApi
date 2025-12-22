using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Services.Popularity;

namespace Relisten.Controllers
{
    [Route("api")]
    [Produces("application/json")]
    public class PopularityController : RelistenBaseController
    {
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
        [ProducesResponseType(typeof(PopularShowListItem[]), 200)]
        public async Task<IActionResult> PopularShows([FromQuery] int limit = 50)
        {
            return JsonSuccess(await popularityService.GetPopularShows(limit));
        }

        [HttpGet("v3/trending/shows")]
        [ProducesResponseType(typeof(PopularShowListItem[]), 200)]
        public async Task<IActionResult> TrendingShows([FromQuery] int limit = 50)
        {
            return JsonSuccess(await popularityService.GetTrendingShows(limit));
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
