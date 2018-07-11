using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Data;
using Hangfire;
using Microsoft.AspNetCore.Authorization;

namespace Relisten.Controllers
{
    [Route("api/v2/import/")]
    [ApiExplorerSettings(IgnoreApi=true)]
    public class ImportController : RelistenBaseController
    {
        protected ImporterService _importer { get; set; }
		protected ScheduledService _scheduledService { get; set; }

		public ImportController(
            RedisService redis,
            DbService db,
			ArtistService artistService,
            ImporterService importer,
			ScheduledService scheduledService
		) : base(redis, db, artistService)
        {
			_scheduledService = scheduledService;
			_importer = importer;
        }

        [HttpGet("{idOrSlug}")]
        [Authorize]
		public async Task<IActionResult> Get(string idOrSlug, [FromQuery] bool deleteOldContent = false)
        {
			Artist art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
				var jobId = BackgroundJob.Enqueue(() => _scheduledService.RefreshArtist(idOrSlug, deleteOldContent, null));
				
				return JsonSuccess($"Queued as job {jobId}!");
            }

            return JsonNotFound(false);
        }

		[HttpGet("{idOrSlug}/debug")]
		[Authorize]
		public async Task<IActionResult> GetDebug(string idOrSlug, [FromQuery] bool deleteOldContent = false)
		{
			Artist art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
			if (art != null)
			{
				await _scheduledService.RefreshArtist(idOrSlug, deleteOldContent, null);

				return JsonSuccess("done!");
			}

			return JsonNotFound(false);
		}

		[HttpGet("{idOrSlug}/{showIdentifier}")]
		[Authorize]
		public async Task<IActionResult> GetDebug(string idOrSlug, string showIdentifier, [FromQuery] bool deleteOldContent = false)
		{
			Artist art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
			if (art != null)
			{
				var jobId = BackgroundJob.Enqueue(() => _scheduledService.RefreshArtist(idOrSlug, showIdentifier, deleteOldContent, null));

				return JsonSuccess($"Queued as job {jobId}!");
			}

			return JsonNotFound(false);
		}

        // private void update()
        // {
        //     var updateDates = @"
        //     update shows s set updatedat = COALESCE((select MAX(updatedat) from tracks t WHERE t.showid = s.id), s.createdat);
        //     update years y set updatedat = COALESCE((select MAX(updatedat) from shows s WHERE s.year = y.year), y.createdat);
        //     update artists a set updatedat = COALESCE((select MAX(updatedat) from years y WHERE y.artistid = a.id), a.createdat);
        //     ";
        // }
    }
}
