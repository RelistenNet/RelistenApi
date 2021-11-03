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
    public class ErasController : RelistenBaseController
    {
        protected EraService _eraService;

        public ErasController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            EraService eraService
        ) : base(redis, db, artistService)
        {
            _eraService = eraService;
        }

        [HttpGet("{artistIdOrSlug}/eras")]
        [ProducesResponseType(typeof(IEnumerable<Era>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> eras(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, art =>
            {
                return _eraService.AllForArtist(art);
            });
        }
    }
}
