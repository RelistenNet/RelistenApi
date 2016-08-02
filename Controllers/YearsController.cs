using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api/2/artists")]
    public class YearsController : RelistenBaseController
    {
        protected ShowService _showService;
        protected SourceService _sourceService;
        protected YearService _yearService;

        public YearsController(
            RedisService redis,
            DbService db,
            ShowService showService,
            SourceService sourceService,
            YearService yearService
        ) : base(redis, db) {
            _showService = showService;
            _yearService = yearService;
            _sourceService = sourceService;
        }

        [HttpGet("{artistIdOrSlug}/years")]
        public async Task<IActionResult> years(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, (art) => {
                return _yearService.AllForArtist(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/years/{year}")]
        public async Task<IActionResult> years(string artistIdOrSlug, string year)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, year, (art, id) => {
                return _yearService.ForIdentifierWithShows(art, id);
            }, true, true);
        }

        [HttpGet("{artistIdOrSlug}/years/{year}/{showDate}")]
        public async Task<IActionResult> years(string artistIdOrSlug, string year, string showDate)
        {
            return await ApiRequest(artistIdOrSlug, async (art) => {
                return await _showService.ShowWithSourcesForArtistOnDate(art, showDate);
            });
        }
    }
}
