using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    [Produces("application/json")]
    public class ShowsController : RelistenBaseController
    {
        public ShowService _showService { get; set; }

        public ShowsController(
            RedisService redis,
            DbService db,
            ShowService showService
        ) : base(redis, db)
        {
            _showService = showService;
        }

        [HttpGet("shows/today")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<ShowWithArtist>>), 200)]
        public async Task<IActionResult> Today()
        {
            return JsonSuccess(await _showService.ShowsForCriteriaWithArtists(@"
                EXTRACT(month from s.date) = EXTRACT(month from NOW())
	            AND EXTRACT(day from s.date) = EXTRACT(day from NOW())
            ", new { }));
        }

        [HttpGet("shows/on-date")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<ShowWithArtist>>), 200)]
        public async Task<IActionResult> OnDayInHistory(int month, int day)
        {
            return JsonSuccess(await _showService.ShowsForCriteriaWithArtists(@"
                EXTRACT(month from s.date) = @month
	            AND EXTRACT(day from s.date) = @day
            ", new { month, day }));
        }

        [HttpGet("artists/{artistIdOrSlug}/shows/today")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<ShowWithArtist>>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> TodayArtist(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, (art) => _showService.ShowsForCriteriaWithArtists(@"
                s.artist_id = @artistId
                AND EXTRACT(month from s.date) = EXTRACT(month from NOW())
	            AND EXTRACT(day from s.date) = EXTRACT(day from NOW())
            ", new { artistId = art.id }));
        }

        [HttpGet("artists/{artistIdOrSlug}/shows/on-date")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<ShowWithArtist>>), 200)]
        public async Task<IActionResult> ArtistOnDayInHistory(int month, int day)
        {
            return JsonSuccess(await _showService.ShowsForCriteriaWithArtists(@"
                s.artist_id = @artistId
                AND EXTRACT(month from s.date) = @month
	            AND EXTRACT(day from s.date) = @day
            ", new { month, day }));
        }

        [HttpGet("artists/{artistIdOrSlug}/shows/top")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<Show>>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> TopByArtist(string artistIdOrSlug, int limit = 25)
        {
            return await ApiRequest(artistIdOrSlug, (art) => _showService.ShowsForCriteria(art, @"
                s.artist_id = @artistId
            ", new { artistId = art.id, limit }, "cnt.max_avg_rating_weighted DESC LIMIT @limit"));
        }

        [HttpGet("artists/{artistIdOrSlug}/shows/random")]
        [ProducesResponseType(typeof(ResponseEnvelope<ShowWithSources>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> RandomByArtist(string artistIdOrSlug)
        {
            return await ApiRequest(artistIdOrSlug, async (art) =>
            {
                var randShow = await db.WithConnection(con => con.QuerySingleAsync<Show>(@"
                    SELECT
                        *
                    FROM
                        shows
                    WHERE
                        artist_id = @id
                    ORDER BY
                        RANDOM()
                    LIMIT 1
                ", art));

                return await _showService.ShowWithSourcesForArtistOnDate(art, randShow.display_date);
            });
        }

        [HttpGet("artists/{artistIdOrSlug}/shows/{showDate}")]
        [ProducesResponseType(typeof(ResponseEnvelope<ShowWithSources>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> ShowsOnSpecificDate(string artistIdOrSlug, string showDate)
        {
            return await ApiRequest(artistIdOrSlug, (art) => _showService.ShowWithSourcesForArtistOnDate(art, showDate));
        }

    }
}
