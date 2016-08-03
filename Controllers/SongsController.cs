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
using Relisten.Api.Models.Api;

namespace Relisten.Controllers
{
    [Route("api/v2/artists")]
    [Produces("application/json")]
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
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<SetlistSongWithPlayCount>>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Songs(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, (art) =>
            {
                return _setlistSongService.AllForArtistWithPlayCount(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/songs/{idAndSlug}")]
        [ProducesResponseType(typeof(ResponseEnvelope<SetlistSongWithShows>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> years(string artistIdOrSlug, string idAndSlug)
        {
            return await ApiRequestWithIdentifier(artistIdOrSlug, idAndSlug, (artist, id) =>
            {
                return _setlistSongService.ForIdWithShows(artist, id.Id.Value);
            });
        }
    }
}
