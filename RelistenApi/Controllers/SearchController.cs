using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Services.Search;
using Relisten.Services.Search.Models;

namespace Relisten.Controllers
{
    [Route("api")]
    [Produces("application/json")]
    public class SearchController : RelistenBaseController
    {
        private readonly SearchService _searchService;
        private readonly HybridSearchService _hybridSearchService;

        public SearchController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            SearchService searchService,
            HybridSearchService hybridSearchService
        ) : base(redis, db, artistService)
        {
            _searchService = searchService;
            _hybridSearchService = hybridSearchService;
        }

        /// <summary>
        /// Legacy keyword search (ILIKE). Returns categorized results by entity type.
        /// </summary>
        [HttpGet("v2/search")]
        [ProducesResponseType(typeof(SearchResults), 200)]
        public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int? artist_id = null)
        {
            if (q.Length < 3)
            {
                return JsonSuccess(new SearchResults
                {
                    Artists = new List<SlimArtist>(),
                    Shows = new List<ShowWithSlimArtist>(),
                    Source = new List<SourceWithSlimArtist>(),
                    Tours = new List<TourWithSlimArtist>(),
                    Venues = new List<VenueWithSlimArtist>()
                });
            }

            return JsonSuccess(await _searchService.Search(q, artist_id));
        }

        /// <summary>
        /// Hybrid semantic + keyword search with Reciprocal Rank Fusion.
        /// Returns show-level results ranked by relevance, with the best-matching source per show.
        /// </summary>
        [HttpGet("v3/search")]
        [ProducesResponseType(typeof(HybridSearchResponse), 200)]
        public async Task<IActionResult> HybridSearch(
            [FromQuery] string q,
            [FromQuery] int? artist_id = null,
            [FromQuery] short? year = null,
            [FromQuery] bool? soundboard = null,
            [FromQuery] string sort = "relevance",
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            {
                return JsonSuccess(new HybridSearchResponse { Query = q ?? "" });
            }

            // Clamp limit to prevent abuse
            if (limit > 100) limit = 100;
            if (limit < 1) limit = 1;
            if (offset < 0) offset = 0;

            var request = new HybridSearchRequest
            {
                Query = q,
                ArtistId = artist_id,
                Year = year,
                Soundboard = soundboard,
                Sort = sort,
                Limit = limit,
                Offset = offset,
            };

            return JsonSuccess(await _hybridSearchService.SearchAsync(request, ct));
        }
    }
}
