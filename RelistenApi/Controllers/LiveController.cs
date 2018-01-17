using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
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

		readonly SourceService sourceService;

		public LiveController(
			RedisService redis,
			DbService db,
			ArtistService artistService,
			ShowService showService,
			SourceService sourceService,
			SourceTrackService sourceTrackService
		) : base(redis, db, artistService)
		{
			this.sourceService = sourceService;
			_redisService = redis;
            _showService = showService;
            _sourceTrackService = sourceTrackService;
        }


        [HttpPost("live/play")]
        [ProducesResponseType(typeof(bool), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
		public async Task<IActionResult> PlayedTrack([FromQuery] int track_id)
        {
            var track = await _sourceTrackService.ForId(track_id);

            if(track == null)
            {
                return JsonNotFound(false);
            }

			await redis.db.SortedSetAddAsync("played", track_id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            return JsonSuccess(true);
        }

		[HttpPost("live/recently-played")]
		[ProducesResponseType(typeof(IEnumerable<PlayedSourceTrack>), 200)]
		public async Task<IActionResult> RecentlyPlayed()
		{
			var trackIds = (await redis.db.SortedSetRangeByRankAsync("played", -26, -1, Order.Descending)).Select(t =>
			{
				t.TryParse(out int id);
				return id;
			});

			var tracks = await _sourceTrackService.ForIds(trackIds.ToList());

			var sourceIds = tracks
				.Select(t => t.source_id)
				.GroupBy(t => t)
				.Select(g => g.First());

			var sources = (await sourceService.SlimSourceWithShowAndArtistForIds(sourceIds.ToList()))
				.GroupBy(s => s.id)
				.ToDictionary(g => g.Key, g => g.First());

			return JsonSuccess(tracks.Select(t => new PlayedSourceTrack
			{
				source = sources[t.source_id],
				track = t
			}));
		}
    }
}
