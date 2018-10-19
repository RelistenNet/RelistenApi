using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Newtonsoft.Json;
using Microsoft.ApplicationInsights;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    [Produces("application/json")]
    public class LiveController : RelistenBaseController
    {
        protected ShowService _showService { get; }
        protected SourceTrackService _sourceTrackService { get; }
        protected SourceTrackPlaysService _sourceTrackPlaysService { get; }
        protected RedisService _redisService { get; }
        protected SourceService _sourceService { get; }

        public LiveController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            ShowService showService,
            SourceService sourceService,
            SourceTrackService sourceTrackService,
            SourceTrackPlaysService sourceTrackPlaysService
        ) : base(redis, db, artistService)
        {
            _sourceService = sourceService;
            _redisService = redis;
            _showService = showService;
            _sourceTrackService = sourceTrackService;
            _sourceTrackPlaysService = sourceTrackPlaysService;
        }


        [HttpPost("live/play")]
        [ProducesResponseType(typeof(ResponseEnvelope<SourceTrackPlay>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        [ProducesResponseType(typeof(void), 400)]
        public async Task<IActionResult> PlayedTrack(
            [FromQuery] string app_type,
            [FromQuery] int? track_id = null,
            [FromQuery] string track_uuid = null,
            [FromQuery] string user_uuid = null
        )
        {
            if (app_type != "ios" && app_type != "web" && app_type != "sonos")
            {
                return BadRequest();
            }

            if (track_id == null && track_uuid == null)
            {
                return BadRequest();
            }

            Guid track_guid = Guid.Empty;
            if (track_uuid != null && !Guid.TryParse(track_uuid, out track_guid))
            {
                return BadRequest("Invalid track_uuid format");
            }

            var telementry = new TelemetryClient();
            telementry.TrackEvent("played_track", new Dictionary<string, string> {
                { "app_type", app_type },
            });

            SourceTrack track = null;

            if (track_uuid != null && track_guid != Guid.Empty)
            {
                track = await _sourceTrackService.ForUUID(track_guid);
            }
            else if (track_id != null)
            {
                track = await _sourceTrackService.ForId(track_id.Value);
            }

            if (track == null)
            {
                return JsonNotFound(false);
            }

            var stp = new SourceTrackPlay
            {
                source_track_uuid = track.uuid,
                app_type = SourceTrackPlayAppTypeHelper.FromString(app_type),
                user_uuid = user_uuid != null ? Guid.Parse(user_uuid) : (Guid?)null
            };

            var lp = new LivePlayedTrack
            {
                played_at = DateTime.UtcNow,
                track_id = track.id,
                app_type = app_type,
                uuid = Guid.NewGuid()
            };

            await redis.db.SortedSetAddAsync("played", JsonConvert.SerializeObject(lp), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return JsonSuccess(await _sourceTrackPlaysService.RecordPlayedTrack(stp));
        }

        [HttpGet("live/recently-played")]
        [ProducesResponseType(typeof(IEnumerable<PlayedSourceTrack>), 200)]
        public async Task<IActionResult> RecentlyPlayed()
        {
            var tracksPlays = (await redis.db
                .SortedSetRangeByScoreAsync("played", order: StackExchange.Redis.Order.Descending, take: 25))
                .Select(t => JsonConvert.DeserializeObject<LivePlayedTrack>(t));

            var tracks = await _sourceTrackService.ForIds(tracksPlays.Select(t => t.track_id).ToList());

            var sourceIds = tracks
                .Select(t => t.source_id)
                .GroupBy(t => t)
                .Select(g => g.First());

            var trackLookup = tracks
                .ToDictionary(t => t.id, t => t)
                ;

            var sources = (await _sourceService.SlimSourceWithShowAndArtistForIds(sourceIds.ToList()))
                .GroupBy(s => s.id)
                .ToDictionary(g => g.Key, g => g.First());

            return JsonSuccess(tracksPlays
                .Where(t => trackLookup.ContainsKey(t.track_id) && sources.ContainsKey(trackLookup[t.track_id].source_id))
                .Select(t =>
            {
                var track = trackLookup[t.track_id];

                t.track = new PlayedSourceTrack
                {
                    source = sources[track.source_id],
                    track = track
                };

                return t;
            }));
        }

        [HttpGet("live/history")]
        [ProducesResponseType(typeof(IEnumerable<SourceTrackPlay>), 200)]
        public async Task<IActionResult> RecentlyPlayed(int? lastSeenId = null, int limit = 20)
        {
            limit = Math.Max(limit, 2000);
            var tracksPlays = (await _sourceTrackPlaysService.PlayedTracksSince(lastSeenId, limit));

            var tracks = await _sourceTrackService.ForUUIDs(tracksPlays.Select(t => t.source_track_uuid).ToList());

            var sourceIds = tracks
                .Select(t => t.source_id)
                .GroupBy(t => t)
                .Select(g => g.First());

            var trackLookup = tracks
                .ToDictionary(t => t.uuid, t => t)
                ;

            var sources = (await _sourceService.SlimSourceWithShowAndArtistForIds(sourceIds.ToList()))
                .GroupBy(s => s.id)
                .ToDictionary(g => g.Key, g => g.First());

            return JsonSuccess(tracksPlays
                .Where(t => trackLookup.ContainsKey(t.source_track_uuid) && sources.ContainsKey(trackLookup[t.source_track_uuid].source_id))
                .Select(trackPlay =>
            {
                var track = trackLookup[trackPlay.source_track_uuid];

                trackPlay.track = new PlayedSourceTrack
                {
                    source = sources[track.source_id],
                    track = track
                };

                return trackPlay;
            }));
        }
    }
}
