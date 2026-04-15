using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Services.Popularity;

namespace Relisten.Controllers
{
    [Route("api")]
    [Produces("application/json")]
    public class CollectionsController : RelistenBaseController
    {
        private const int MaxShowLimit = 25;
        private readonly CollectionService collectionService;

        public CollectionsController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            CollectionService collectionService
        ) : base(redis, db, artistService)
        {
            this.collectionService = collectionService;
        }

        [HttpGet("v3/collections")]
        [ProducesResponseType(typeof(IEnumerable<CollectionSummary>), 200)]
        public async Task<IActionResult> GetCollections()
        {
            return JsonSuccess(await collectionService.AllCollections());
        }

        [HttpGet("v3/collections/{collectionUuidOrSlug}")]
        [ProducesResponseType(typeof(CollectionDetail), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> GetCollection(string collectionUuidOrSlug)
        {
            return await CollectionRequest(collectionUuidOrSlug, collection => Task.FromResult(collection));
        }

        [HttpGet("v3/collections/{collectionUuidOrSlug}/artists")]
        [ProducesResponseType(typeof(IEnumerable<ArtistWithCounts>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> GetArtists(string collectionUuidOrSlug)
        {
            return await CollectionRequest(collectionUuidOrSlug, collectionService.ArtistsForCollection);
        }

        [HttpGet("v3/collections/{collectionUuidOrSlug}/years")]
        [ProducesResponseType(typeof(IEnumerable<CollectionYear>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> GetYears(string collectionUuidOrSlug)
        {
            return await CollectionRequest(collectionUuidOrSlug, collectionService.YearsForCollection);
        }

        [HttpGet("v3/collections/{collectionUuidOrSlug}/years/{yearUuidOrYear}")]
        [ProducesResponseType(typeof(CollectionYearWithShows), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> GetYear(string collectionUuidOrSlug, string yearUuidOrYear)
        {
            return await CollectionRequest(collectionUuidOrSlug,
                collection => collectionService.YearWithShowsForCollection(collection, yearUuidOrYear));
        }

        [HttpGet("v3/collections/{collectionUuidOrSlug}/shows/popular-trending")]
        [ProducesResponseType(typeof(CollectionPopularTrendingShowsResponse), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> GetPopularTrendingShows(string collectionUuidOrSlug,
            [FromQuery] int limit = MaxShowLimit,
            [ModelBinder(BinderType = typeof(PopularitySortWindowModelBinder))]
            [FromQuery] PopularitySortWindow window = PopularitySortWindow.Days30)
        {
            return await CollectionRequest(collectionUuidOrSlug,
                collection => collectionService.PopularTrendingShowsForCollection(collection,
                    NormalizeShowLimit(limit), window));
        }

        [HttpGet("v3/collections/{collectionUuidOrSlug}/shows/recently-added")]
        [ProducesResponseType(typeof(IEnumerable<Show>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> GetRecentlyAddedShows(string collectionUuidOrSlug,
            [FromQuery] int limit = MaxShowLimit)
        {
            return await CollectionRequest(collectionUuidOrSlug,
                collection => collectionService.RecentlyAddedShowsForCollection(collection,
                    NormalizeShowLimit(limit)));
        }

        [HttpGet("v3/collections/{collectionUuidOrSlug}/shows/on-this-day")]
        [ProducesResponseType(typeof(IEnumerable<Show>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 400)]
        public async Task<IActionResult> GetOnThisDay(string collectionUuidOrSlug, [FromQuery] int month,
            [FromQuery] int day)
        {
            if (!CollectionService.IsValidMonthDay(month, day))
            {
                return BadRequest(ResponseEnvelope<bool>.Error(ApiErrorCode.BadRequest, false));
            }

            return await CollectionRequest(collectionUuidOrSlug,
                collection => collectionService.OnThisDayForCollection(collection, month, day));
        }

        private async Task<IActionResult> CollectionRequest<T>(string collectionUuidOrSlug,
            Func<CollectionDetail, Task<T>> callback)
        {
            var collection = await collectionService.FindCollection(collectionUuidOrSlug);
            if (collection == null)
            {
                return JsonNotFound(false);
            }

            var result = await callback(collection);
            if (result == null)
            {
                return JsonNotFound(false);
            }

            return JsonSuccess(result);
        }

        private static int NormalizeShowLimit(int limit)
        {
            if (limit <= 0)
            {
                return MaxShowLimit;
            }

            return limit > MaxShowLimit ? MaxShowLimit : limit;
        }
    }
}
