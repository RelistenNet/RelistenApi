﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Newtonsoft.Json;

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

            if (track == null)
            {
                return JsonNotFound(false);
            }

            var lp = new SlimLivePlayedTrack
            {
                played_at = DateTime.UtcNow,
                track_id = track_id
            };

            await redis.db.SortedSetAddAsync("played", JsonConvert.SerializeObject(lp), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            return JsonSuccess(true);
        }

        [HttpGet("live/recently-played")]
        [ProducesResponseType(typeof(IEnumerable<PlayedSourceTrack>), 200)]
        public async Task<IActionResult> RecentlyPlayed()
        {
            var tracksPlays = (await redis.db
                .SortedSetRangeByScoreAsync("played", order: StackExchange.Redis.Order.Descending, take: 25))
                .Select(t => JsonConvert.DeserializeObject<SlimLivePlayedTrack>(t));

            var tracks = await _sourceTrackService.ForIds(tracksPlays.Select(t => t.track_id).ToList());

            var sourceIds = tracks
                .Select(t => t.source_id)
                .GroupBy(t => t)
                .Select(g => g.First());

            var trackLookup = tracks
                .ToDictionary(t => t.id, t => t)
                ;

            var sources = (await sourceService.SlimSourceWithShowAndArtistForIds(sourceIds.ToList()))
                .GroupBy(s => s.id)
                .ToDictionary(g => g.Key, g => g.First());

            return JsonSuccess(tracksPlays.Select(t =>
            {
                var track = trackLookup[t.track_id];

                return new LivePlayedTrack
                {
                    track_id = t.track_id,
                    played_at = t.played_at,

                    track = new PlayedSourceTrack
                    {
                        source = sources[track.source_id],
                        track = track
                    }
                };
            }));
        }
    }
}
