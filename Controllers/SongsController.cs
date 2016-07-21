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

namespace Relisten.Controllers
{
    [Route("api/2/artists")]
    public class SongsController : RelistenBaseController
    {
        private SetlistSongService _setlistSongService { get; set; }
        private SetlistShowService _setlistShowService { get; set; }

        public SongsController(
            RedisService redis,
            DbService db,
            SetlistSongService setlistSongService,
            SetlistShowService setlistShowService
        ) : base(redis, db)
        {
            _setlistSongService = setlistSongService;
            _setlistShowService = setlistShowService;
        }

        [HttpGet("{artistIdOrSlug}/songs")]
        public async Task<IActionResult> Songs(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, (art) => {
                return _setlistSongService.AllForArtistWithPlayCount(art);
            });
        }
    }
}
