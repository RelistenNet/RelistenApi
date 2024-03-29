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
    public class YearsController : RelistenBaseController
    {
        protected ShowService _showService;
        protected SourceService _sourceService;
        protected YearService _yearService;

        public YearsController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            ShowService showService,
            SourceService sourceService,
            YearService yearService
        ) : base(redis, db, artistService)
        {
            _showService = showService;
            _yearService = yearService;
            _sourceService = sourceService;
        }

        [HttpGet("v2/artists/{artistIdOrSlug}/years")]
        [HttpGet("v3/artists/{artistIdOrSlug}/years")]
        [ProducesResponseType(typeof(IEnumerable<Year>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> years(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, art =>
            {
                return _yearService.AllForArtist(art);
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
            return await ApiRequestWithIdentifier(artistIdOrSlug, yearUuid, (art, id) =>
            {
                return _yearService.ForIdentifierWithShows(art, id);
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
