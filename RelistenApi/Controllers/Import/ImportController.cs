using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Relisten.Api;
using Relisten.Data;
using Relisten.Import;

namespace Relisten.Controllers
{
    [Route("api/v2/import/")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class ImportController : RelistenBaseController
    {
        public ImportController(
            RedisService redis,
            DbService db,
            ArtistService artistService,
            ImporterService importer,
            ScheduledService scheduledService,
            IConfiguration configuration
        ) : base(redis, db, artistService)
        {
            _configuration = configuration;
            _scheduledService = scheduledService;
            _importer = importer;
        }

        protected ImporterService _importer { get; set; }
        protected ScheduledService _scheduledService { get; set; }
        protected IConfiguration _configuration { get; }

        [HttpGet("{idOrSlug}")]
        [Authorize]
        public async Task<IActionResult> Get(string idOrSlug, [FromQuery] bool deleteOldContent = false)
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                var jobId = BackgroundJob.Enqueue(() =>
                    _scheduledService.RefreshArtist(idOrSlug, deleteOldContent, null));

                return JsonSuccess($"Queued as job {jobId}!");
            }

            return JsonNotFound(false);
        }

        [HttpGet("{idOrSlug}/debug")]
        [Authorize]
        public async Task<IActionResult> GetDebug(string idOrSlug, [FromQuery] bool deleteOldContent = false)
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                await _scheduledService.RefreshArtist(idOrSlug, deleteOldContent, null);

                return JsonSuccess("done!");
            }

            return JsonNotFound(false);
        }

        [HttpPost("phishin")]
        public IActionResult JustRefreshPhishin()
        {
            if (!Request.Headers.TryGetValue("X-Relisten-Api-Key", out var key) ||
                key != _configuration["PANIC_KEY"])
            {
                return Unauthorized();
            }

            return JsonSuccess(
                BackgroundJob.Enqueue(() => _scheduledService.RefreshPhishFromPhishinOnly(null)));
        }

        [HttpGet("all-years-and-shows")]
        [Authorize]
        public IActionResult AllYearsAndShows()
        {
            return JsonSuccess(BackgroundJob.Enqueue(() => _scheduledService.RebuildAllArtists(null)));
        }

        [HttpGet("{idOrSlug}/{showIdentifier}")]
        [Authorize]
        public async Task<IActionResult> GetDebug(string idOrSlug, string showIdentifier,
            [FromQuery] bool deleteOldContent = false)
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                var jobId = BackgroundJob.Enqueue(() =>
                    _scheduledService.RefreshArtist(idOrSlug, showIdentifier, deleteOldContent, null));

                return JsonSuccess($"Queued as job {jobId}!");
            }

            return JsonNotFound(false);
        }

        [HttpGet("{idOrSlug}/{showIdentifier}/debug")]
        [Authorize]
        public async Task<IActionResult> GetSingleDebug(string idOrSlug, string showIdentifier,
            [FromQuery] bool deleteOldContent = false)
        {
            var art = await _artistService.FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                await _scheduledService.RefreshArtist(idOrSlug, showIdentifier, deleteOldContent, null, null);

                return JsonSuccess("done!");
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
