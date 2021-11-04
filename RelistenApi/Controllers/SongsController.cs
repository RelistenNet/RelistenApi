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
    public class SongsController : RelistenBaseController
    {
        public SongsController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            SetlistSongService setlistSongService,
            SetlistShowService setlistShowService
        ) : base(redis, db, artistService)
        {
            _setlistSongService = setlistSongService;
            _setlistShowService = setlistShowService;
        }

        private SetlistSongService _setlistSongService { get; }
        private SetlistShowService _setlistShowService { get; }

        [HttpGet("{artistIdOrSlug}/songs")]
        [ProducesResponseType(typeof(IEnumerable<SetlistSongWithPlayCount>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Songs(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, art =>
            {
                return _setlistSongService.AllForArtistWithPlayCount(art);
            });
        }

        [HttpGet("{artistIdOrSlug}/songs/{idAndSlug}")]
        [ProducesResponseType(typeof(SetlistSongWithShows), 200)]
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
