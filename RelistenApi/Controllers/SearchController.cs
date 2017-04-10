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
		public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int? artist_id = null)
		{
			if(q.Length < 3)
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
	}
}
