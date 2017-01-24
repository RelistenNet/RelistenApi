using System;
using Relisten.Api;
using Relisten.Data;

namespace Relisten.Controllers
{
	public class RecentController : RelistenBaseController
	{
		public SourceService _sourceService { get; set; }

		public RecentController(
			RedisService redis,
			DbService db,
			ArtistService artistService,
			SourceService sourceService
		) : base(redis, db, artistService)
        {
			_sourceService = sourceService;
		}
	}
}
