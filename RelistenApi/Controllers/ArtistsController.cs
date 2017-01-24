using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Api.Models.Api;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    [Produces("application/json")]
    public class ArtistsController : RelistenBaseController
    {
		public ArtistsController(
            RedisService redis,
            DbService db,
			ArtistService artistService
		) : base(redis, db, artistService)
        {
        }

        // GET api/values
        [HttpGet("artists")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<Artist>>), 200)]
        public async Task<IActionResult> Get()
        {
			return JsonSuccess(await _artistService.AllWithCounts());
        }

        // GET api/values/5
        [HttpGet("artists/{artistIdOrSlug}")]
        [ProducesResponseType(typeof(ResponseEnvelope<Artist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Get(string artistIdOrSlug)
        {
			Artist art = await _artistService.FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                return JsonSuccess(art);
            }

            return JsonNotFound(false);
        }
    }
}
