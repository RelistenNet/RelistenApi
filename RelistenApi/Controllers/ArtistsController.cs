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
using Microsoft.AspNetCore.Authorization;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    [Produces("application/json")]
    public class ArtistsController : RelistenBaseController
    {
        readonly UpstreamSourceService upstreamSourceService;

        public ArtistsController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            UpstreamSourceService upstreamSourceService
        ) : base(redis, db, artistService)
        {
            this.upstreamSourceService = upstreamSourceService;
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

		[ApiExplorerSettings(IgnoreApi = true)]
		[HttpPost("artists")]
        [Authorize]
		public async Task<IActionResult> CreateArtist([FromBody] CreateUpdateArtistDto artist)
		{
            artist.SlimArtist.id = 0;

            var art = await _artistService.Save(artist.SlimArtist);
            await upstreamSourceService.ReplaceUpstreamSourcesForArtist(art, artist.SlimUpstreamSources);

            return JsonSuccess(await _artistService.FindArtistById(art.id));
		}

		[ApiExplorerSettings(IgnoreApi = true)]
        [HttpPut("artists/{artistIdOrSlug}")]
		[Authorize]
		public async Task<IActionResult> UpdateArtist([FromBody] CreateUpdateArtistDto artist)
		{
			var art = await _artistService.Save(artist.SlimArtist);
			await upstreamSourceService.ReplaceUpstreamSourcesForArtist(art, artist.SlimUpstreamSources);

			return JsonSuccess(await _artistService.FindArtistById(art.id));
		}
	}

    public class CreateUpdateArtistDto {
        public SlimArtistWithFeatures SlimArtist { get; set; }
        public IEnumerable<SlimArtistUpstreamSource> SlimUpstreamSources { get; set; }
    }
}
