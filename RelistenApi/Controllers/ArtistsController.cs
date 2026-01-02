using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Relisten.Api;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Services.Popularity;

namespace Relisten.Controllers
{
    [Produces("application/json")]
    [Route("api")]
    public class ArtistsController : RelistenBaseController
    {
        private readonly SetlistSongService setlistSongService;
        private readonly ShowService showService;
        private readonly SourceService sourceService;
        private readonly TourService tourService;
        private readonly UpstreamSourceService upstreamSourceService;
        private readonly VenueService venueService;
        private readonly YearService yearService;
        private readonly PopularityService popularityService;

        public ArtistsController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            YearService yearService,
            VenueService venueService,
            SetlistSongService setlistSongService,
            TourService tourService,
            UpstreamSourceService upstreamSourceService,
            ShowService showService,
            SourceService sourceService,
            RedisService redisService,
            PopularityService popularityService
        ) : base(redis, db, artistService)
        {
            this.yearService = yearService;
            this.venueService = venueService;
            this.setlistSongService = setlistSongService;
            this.tourService = tourService;
            this.upstreamSourceService = upstreamSourceService;
            this.showService = showService;
            this.sourceService = sourceService;
            this.popularityService = popularityService;
        }

        [HttpGet("v2/artists")]
        [HttpGet("v3/artists")]
        [ProducesResponseType(typeof(IEnumerable<ArtistWithCounts>), 200)]
        public async Task<IActionResult> Get([FromQuery(Name = "include_autocreated")] bool includeAutoCreated = false)
        {
            var artists = (await _artistService.AllWithCounts<ArtistWithCounts>(
                includeAutoCreated: includeAutoCreated)).ToList();

            if (IsV3Request)
            {
                var popularity = await popularityService.GetArtistPopularityMap();
                popularityService.ApplyArtistPopularity(artists, popularity);
            }

            return JsonSuccess(artists);
        }

        [HttpGet("v2/artists/{artistIdOrSlug}")]
        [HttpGet("v3/artists/{artistIdOrSlug}")]
        [ProducesResponseType(typeof(ArtistWithCounts), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Get(string artistIdOrSlug)
        {
            var art = (await _artistService.AllWithCounts<ArtistWithCounts>(new List<string> {artistIdOrSlug}))
                .FirstOrDefault();
            if (art != null)
            {
                if (IsV3Request)
                {
                    var popularity = await popularityService.GetArtistPopularityMap();
                    popularityService.ApplyArtistPopularity(new List<ArtistWithCounts> {art}, popularity);
                }

                return JsonSuccess(art);
            }

            return JsonNotFound(false);
        }

        public static string FullArtistCacheKey(Artist art)
        {
            return $"full-artist:{art.uuid.ToString()}";
        }

        [HttpGet("v3/artists/{artistUuid}/normalized")]
        [ProducesResponseType(typeof(FullArtist), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> GetFullArtist(string artistUuid)
        {
            var art = (await _artistService.AllWithCounts<ArtistWithCounts>(new List<string> {artistUuid}))
                .FirstOrDefault();

            if (art == null)
            {
                return JsonNotFound(false);
            }

            var cacheKey = FullArtistCacheKey(art);
            var cacheResult = await redis.db.StringGetAsync(cacheKey);

            if (cacheResult.HasValue && !cacheResult.IsNullOrEmpty)
            {
                return Content(cacheResult.ToString(), "application/json");
            }

            var yearsTask = yearService.AllForArtist(art);
            var venuesTask = venueService.AllForArtist(art);
            var songsTask = setlistSongService.AllForArtistWithPlayCount(art);
            var toursTask = tourService.AllForArtistWithShowCount(art);
            var showsTask = showService.ShowsForCriteria(art, "s.artist_id = @artistId", new {artistId = art.id},
                includeNestedObject: false);

            await Task.WhenAll(yearsTask, venuesTask, songsTask, toursTask, showsTask);

            var resp = new FullArtist()
            {
                artist = art,
                years = yearsTask.Result.ToList(),
                venues = venuesTask.Result.ToList(),
                songs = songsTask.Result.ToList(),
                tours = toursTask.Result.ToList(),
                shows = showsTask.Result.ToList(),
            };

            var artistPopularity = await popularityService.GetArtistPopularityMap();
            popularityService.ApplyArtistPopularity(new List<ArtistWithCounts> {resp.artist}, artistPopularity);

            var yearPopularity = await popularityService.GetYearPopularityMapForArtist(art.uuid);
            popularityService.ApplyYearPopularity(resp.years, yearPopularity);

            var json = JsonConvert.SerializeObject(
                resp, RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings);

            await redis.db.StringSetAsync(cacheKey, json, TimeSpan.FromHours(24));

            return Content(json, "application/json");
        }

        [ApiExplorerSettings(IgnoreApi = true)]
        [HttpPost("v2/artists")]
        [Authorize]
        public async Task<IActionResult> CreateArtist([FromBody] CreateUpdateArtistDto artist)
        {
            artist.SlimArtist.id = 0;

            var art = await _artistService.Save(artist.SlimArtist);
            await upstreamSourceService.ReplaceUpstreamSourcesForArtist(art, artist.SlimUpstreamSources);

            return JsonSuccess(await _artistService.FindArtistById(art.id));
        }

        [ApiExplorerSettings(IgnoreApi = true)]
        [HttpPut("v2/artists/{artistIdOrSlug}")]
        [Authorize]
        public async Task<IActionResult> UpdateArtist([FromBody] CreateUpdateArtistDto artist)
        {
            var art = await _artistService.Save(artist.SlimArtist);
            await upstreamSourceService.ReplaceUpstreamSourcesForArtist(art, artist.SlimUpstreamSources);

            return JsonSuccess(await _artistService.FindArtistById(art.id));
        }
    }

    public class CreateUpdateArtistDto
    {
        public SlimArtistWithFeatures SlimArtist { get; set; } = null!;
        public IEnumerable<SlimArtistUpstreamSource> SlimUpstreamSources { get; set; } = null!;
    }
}
