using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    [Produces("application/json")]
    public class SearchController : RelistenBaseController
    {
        private readonly SearchService _searchService;

        public SearchController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            SearchService searchService
        ) : base(redis, db, artistService)
        {
            _searchService = searchService;
        }

        [HttpGet("search")]
        [ProducesResponseType(typeof(SearchResults), 200)]
        public async Task<IActionResult> Search(
            [FromQuery] string? q,
            [FromQuery] Guid? artist_uuid = null,
            [FromQuery] bool artists = true,
            [FromQuery] bool shows = true,
            [FromQuery] bool songs = true,
            [FromQuery] bool sources = true,
            [FromQuery] bool tours = true,
            [FromQuery] bool venues = true)
        {
            var searchTerm = q?.Trim();
            var options = new SearchOptions
            {
                Artists = artists,
                Shows = shows,
                Songs = songs,
                Sources = sources,
                Tours = tours,
                Venues = venues
            };

            if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Length < 3 || !options.AnyEnabled)
            {
                return JsonSuccess(SearchResults.Empty());
            }

            return JsonSuccess(await _searchService.Search(searchTerm, artist_uuid, options));
        }
    }
}
