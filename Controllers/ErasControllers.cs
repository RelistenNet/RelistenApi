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
    public class ErasController : RelistenBaseController
    {
        protected ShowService _showService;
        protected YearService _yearService;

        public ErasController(
            RedisService redis,
            DbService db,
            ShowService showService,
            YearService yearService
        ) : base(redis, db) {
            _showService = showService;
            _yearService = yearService;
        }

        [HttpGet("{artistIdOrSlug}/eras")]
        public async Task<IActionResult> years(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, (art) => {
                return _yearService.AllForArtist(art);
            });
        }

    }
}
