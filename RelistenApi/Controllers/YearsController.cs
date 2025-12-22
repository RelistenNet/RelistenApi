using System.Collections.Generic;
using System.Linq;
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
    public class YearsController : RelistenBaseController
    {
        protected ShowService _showService;
        protected SourceService _sourceService;
        protected YearService _yearService;
        private readonly PopularityService popularityService;

        public YearsController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            ShowService showService,
            SourceService sourceService,
            YearService yearService,
            PopularityService popularityService
        ) : base(redis, db, artistService)
        {
            _showService = showService;
            _yearService = yearService;
            _sourceService = sourceService;
            this.popularityService = popularityService;
        }

        [HttpGet("v2/artists/{artistIdOrSlug}/years")]
        [HttpGet("v3/artists/{artistIdOrSlug}/years")]
        [ProducesResponseType(typeof(IEnumerable<Year>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> years(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, async art =>
            {
                var years = (await _yearService.AllForArtist(art)).ToList();

                if (IsV3Request)
                {
                    var popularity = await popularityService.GetYearPopularityMapForArtist(art.uuid);
                    popularityService.ApplyYearPopularity(years, popularity);
                }

                return years;
            });
        }

        [HttpGet("v2/artists/{artistIdOrSlug}/years/{year}")]
        [ProducesResponseType(typeof(YearWithShows), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> years(string artistIdOrSlug, string year)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, year, (art, id) =>
            {
                return _yearService.ForIdentifierWithShows(art, id);
            }, true, true);
        }

        [HttpGet("v3/artists/{artistIdOrSlug}/years/{yearUuid}")]
        [ProducesResponseType(typeof(YearWithShows), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> yearByUuid(string artistIdOrSlug, string yearUuid)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, yearUuid, async (art, id) =>
            {
                var year = await _yearService.ForIdentifierWithShows(art, id);
                if (year != null && IsV3Request)
                {
                    var popularity = await popularityService.GetYearPopularityMapForArtist(art.uuid);
                    popularityService.ApplyYearPopularity(new List<Year> {year}, popularity);
                }

                return year;
            }, true, false);
        }

        [HttpGet("v2/artists/{artistIdOrSlug}/years/{year}/{showDate}")]
        [ProducesResponseType(typeof(ShowWithSources), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> years(string artistIdOrSlug, string year, string showDate)
        {
            return await ApiRequest(artistIdOrSlug,
                art => _showService.ShowWithSourcesForArtistOnDate(art, showDate));
        }
    }
}
