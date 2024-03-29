﻿using System;
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

        public SourceService _sourceService { get; set; }
        public ShowService _showService { get; set; }

        [HttpGet("artists/{artistIdOrSlug}/shows/recently-added")]
        [ProducesResponseType(typeof(IEnumerable<ShowWithArtist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> ArtistRecentlyAddedShows([FromRoute] string artistIdOrSlug,
            [FromQuery] int limit = 20, [FromQuery] int? previousDays = null)
        {
            limit = limit > 200 ? 200 : limit;

            return await ApiRequest(artistIdOrSlug, art => _showService.ShowsForCriteria(art, $@"
                s.artist_id = @artistId
				{(previousDays != null ? "AND cnt.max_updated_at >= current_date - '1 day'::interval * @previousDays" : "")}
            ", new {artistId = art.id, previousDays}, limit, "cnt.max_updated_at DESC"));
        }

        [HttpGet("artists/shows/recently-added")]
        [Obsolete]
        [ProducesResponseType(typeof(IEnumerable<ShowWithArtist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> RecentlyAddedShows([FromQuery] int limit = 25)
        {
            limit = limit > 200 ? 200 : limit;

            return JsonSuccess(
                await _showService.ShowsForCriteriaWithArtists(@"1 = 1", null, limit, "cnt.max_updated_at DESC"));
        }

        [HttpGet("shows/recently-added")]
        [ProducesResponseType(typeof(IEnumerable<ShowWithArtist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> RecentlyAddedShows([FromQuery] int limit = 20,
            [FromQuery] int? previousDays = null)
        {
            limit = limit > 200 ? 200 : limit;

            return JsonSuccess(await _showService.ShowsForCriteriaWithArtists($@"
				1 = 1
				{(previousDays != null ? "AND cnt.max_updated_at >= current_date - '1 day'::interval * @previousDays" : "")}
			", new {previousDays}, limit, "cnt.max_updated_at DESC"));
        }
    }
}
