using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    public class RecentController : RelistenBaseController
    {
        public RecentController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            SourceService sourceService,
            ShowService showService
        ) : base(redis, db, artistService)
        {
            _sourceService = sourceService;
            _showService = showService;
        }

        private readonly SourceService _sourceService;
        private readonly ShowService _showService;

        private static string SortColumn(string sortBy)
        {
            return sortBy == "updated_at" ? "cnt.max_updated_at" : "cnt.max_created_at";
        }

        [HttpGet("artists/{artistIdOrSlug}/shows/recently-added")]
        [ProducesResponseType(typeof(IEnumerable<ShowWithArtist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> ArtistRecentlyAddedShows([FromRoute] string artistIdOrSlug,
            [FromQuery] int limit = 20, [FromQuery] int? previousDays = null,
            [FromQuery] string sort_by = "created_at")
        {
            limit = limit > 200 ? 200 : limit;
            if (sort_by != "created_at" && sort_by != "updated_at") sort_by = "created_at";
            var col = SortColumn(sort_by);

            return await ApiRequest(artistIdOrSlug, art => _showService.ShowsForCriteria(art, $@"
                s.artist_id = @artistId
				{(previousDays != null ? $"AND {col} >= current_date - '1 day'::interval * @previousDays" : "")}
            ", new {artistId = art.id, previousDays}, limit, $"{col} DESC"));
        }

        [HttpGet("artists/shows/recently-added")]
        [Obsolete]
        [ProducesResponseType(typeof(IEnumerable<ShowWithArtist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> RecentlyAddedShows([FromQuery] int limit = 25,
            [FromQuery] string sort_by = "created_at")
        {
            limit = limit > 200 ? 200 : limit;
            if (sort_by != "created_at" && sort_by != "updated_at") sort_by = "created_at";
            var col = SortColumn(sort_by);

            return JsonSuccess(
                await _showService.ShowsForCriteriaWithArtists(@"1 = 1", null, limit, $"{col} DESC"));
        }

        [HttpGet("shows/recently-added")]
        [ProducesResponseType(typeof(IEnumerable<ShowWithArtist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> RecentlyAddedShows([FromQuery] int limit = 20,
            [FromQuery] int? previousDays = null,
            [FromQuery] string sort_by = "created_at")
        {
            limit = limit > 200 ? 200 : limit;
            if (sort_by != "created_at" && sort_by != "updated_at") sort_by = "created_at";
            var col = SortColumn(sort_by);

            return JsonSuccess(await _showService.ShowsForCriteriaWithArtists($@"
				1 = 1
				{(previousDays != null ? $"AND {col} >= current_date - '1 day'::interval * @previousDays" : "")}
			", new {previousDays}, limit, $"{col} DESC"));
        }
    }
}
