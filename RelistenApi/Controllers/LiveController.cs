using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    [Produces("application/json")]
    public class LiveController : RelistenBaseController
    {
        public ShowService _showService { get; set; }
        public SourceTrackService _sourceTrackService { get; set; }
		public RedisService _redisService { get; set; }

        public LiveController(
            RedisService redis,
            DbService db,
			ArtistService artistService,
            ShowService showService,
            SourceTrackService sourceTrackService
		) : base(redis, db, artistService)
        {
			_redisService = redis;
            _showService = showService;
            _sourceTrackService = sourceTrackService;
        }


        [HttpPost("live/play")]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
		public async Task<IActionResult> PlayedTrack([FromQuery] int track_id)
        {
            var track = await _sourceTrackService.ForId(track_id);

            if(track == null)
            {
                return JsonNotFound(false);
            }

			//await redis.db.SortedSetAdd("played", )

            return JsonSuccess(true);
        }

		[HttpPost("live/recently-played")]
		[ProducesResponseType(typeof(ResponseEnvelope<bool>), 200)]
		public async Task<IActionResult> RecentlyPlayed()
		{
            await Task.Delay(10);

            return JsonSuccess(true);
		}
    }
}