using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Services.Live;

namespace Relisten.Controllers
{
    [Route("api/v3")]
    [Produces("application/json")]
    public class LiveSummaryController : RelistenBaseController
    {
        private readonly LiveSummaryService liveSummaryService;

        public LiveSummaryController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            LiveSummaryService liveSummaryService
        )
            : base(redis, db, artistService)
        {
            this.liveSummaryService = liveSummaryService;
        }

        /// <summary>
        /// What is being played right now, grouped by show, with per-artist and
        /// per-minute aggregates. Counts are plays (track starts), not listeners.
        /// </summary>
        [HttpGet("live/summary")]
        [ProducesResponseType(typeof(LiveSummary), 200)]
        [ProducesResponseType(typeof(string), 400)]
        public async Task<IActionResult> Summary(
            [FromQuery] int minutes = LiveSummaryRequest.DefaultMinutes,
            [FromQuery] int shows_limit = LiveSummaryRequest.DefaultShowsLimit,
            [FromQuery] int artists_limit = LiveSummaryRequest.DefaultArtistsLimit,
            [FromQuery] string? month_day = null,
            [FromQuery] string? sort = null,
            [FromQuery] int max_per_artist = LiveSummaryRequest.DefaultMaxPerArtist,
            [FromQuery] int fresh_days = LiveSummaryRequest.DefaultFreshDays
        )
        {
            var now = DateTime.UtcNow;
            if (
                !LiveSummaryRequest.TryCreate(
                    minutes,
                    shows_limit,
                    artists_limit,
                    month_day,
                    sort,
                    max_per_artist,
                    fresh_days,
                    now,
                    out var request,
                    out var error
                )
            )
            {
                return BadRequest(error);
            }

            var window = LiveWindow.EndingBefore(now, request.Minutes);
            var json = await liveSummaryService.SummaryJson(request, window, now);

            Response.Headers.CacheControl = SharedCacheControl(window, now);
            return Content(json, "application/json");
        }

        /// <summary>
        /// Lets the CDN serve this response until the next window ends, then serve it
        /// stale for one more interval while it fetches the next one.
        /// ApiCacheControlAttribute keeps a Cache-Control header an action sets itself.
        /// </summary>
        private static string SharedCacheControl(LiveWindow window, DateTime now)
        {
            var secondsUntilNextWindow = Math.Max(
                1,
                (int)Math.Ceiling((window.NextRefreshAt - now).TotalSeconds)
            );
            var staleSeconds = (int)LiveWindow.RefreshInterval.TotalSeconds;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"public, max-age={secondsUntilNextWindow}, stale-while-revalidate={staleSeconds}"
            );
        }
    }
}
